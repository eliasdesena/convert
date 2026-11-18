using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Interop;
using System.Windows.Threading;
using Markdig;
using Microsoft.Web.WebView2.Core;

namespace Convert.Core;

/// <summary>
/// Documents. Markdown, plain text and HTML → PDF/HTML/TXT work with what every Windows 11 has
/// (Markdig + WebView2). Word/ODT/RTF need LibreOffice, like Excel/PowerPoint would.
/// </summary>
public static partial class DocConv
{
    private enum Src { Markdown, Text, Html, Office }

    private static Src SourceOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".md" or ".markdown" => Src.Markdown,
        ".txt" or ".text" => Src.Text,
        ".html" or ".htm" => Src.Html,
        _ => Src.Office,
    };

    public const string LibreOfficeHint = "Needs LibreOffice: winget install TheDocumentFoundation.LibreOffice";

    /// <summary>Whether converting this file to this target needs LibreOffice.</summary>
    public static bool NeedsLibreOffice(string input, Target t) => (SourceOf(input), t.Id) switch
    {
        (Src.Office, _) => true,
        (_, "pdf-doc" or "html") => false,
        (Src.Markdown or Src.Text, "txt") => false,
        _ => true,
    };

    public static async Task<string> ConvertAsync(string input, Target t, ConvSettings s)
    {
        var src = SourceOf(input);
        if (NeedsLibreOffice(input, t) && Tools.LibreOffice == null) throw new ConvertException(LibreOfficeHint);

        var tmp = Paths.TempDir();
        try
        {
            switch (t.Id)
            {
                case "pdf-doc" when src != Src.Office:
                {
                    var html = Path.Combine(tmp, "doc.html");
                    File.WriteAllText(html, PrintableHtml(input, src, s.PaperA4), Encoding.UTF8);
                    var out_ = Paths.UniqueOutput(input, "pdf");
                    await PrintToPdfAsync(html, out_, s.PaperA4);
                    return out_;
                }
                case "html" when src is Src.Markdown or Src.Text or Src.Html:
                {
                    var out_ = Paths.UniqueOutput(input, "html");
                    if (src == Src.Html) File.Copy(input, out_);
                    else File.WriteAllText(out_, StandaloneHtml(input, src, paper: null), Encoding.UTF8);
                    return out_;
                }
                case "txt" when src is Src.Markdown or Src.Text:
                {
                    var out_ = Paths.UniqueOutput(input, "txt");
                    if (src == Src.Text) File.Copy(input, out_);
                    else File.WriteAllText(out_, Markdown.ToPlainText(File.ReadAllText(input), Pipeline).Trim() + "\n", Encoding.UTF8);
                    return out_;
                }
                default:
                {
                    // LibreOffice. Markdown goes in as rendered HTML so headings, lists and tables survive.
                    var feed = input;
                    if (src == Src.Markdown)
                    {
                        feed = Path.Combine(tmp, Path.GetFileNameWithoutExtension(input) + ".html");
                        File.WriteAllText(feed, StandaloneHtml(input, src, paper: null), Encoding.UTF8);
                    }
                    return await LibreOfficeAsync(feed, SourceOf(feed), t, input, tmp);
                }
            }
        }
        finally
        {
            Paths.TryDelete(tmp);
        }
    }

    // Markdown / HTML

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    private const string Css = """
        :root { color-scheme: light; }
        body { font: 11pt/1.55 "Segoe UI", system-ui, sans-serif; color: #1a1a1a; max-width: 46em; margin: 2em auto; padding: 0 1em; }
        @media print { body { max-width: none; margin: 0; padding: 0; } }
        h1, h2, h3, h4 { line-height: 1.25; margin: 1.4em 0 .5em; }
        h1 { font-size: 1.9em; } h2 { font-size: 1.45em; border-bottom: 1px solid #e3e3e3; padding-bottom: .2em; } h3 { font-size: 1.2em; }
        p, ul, ol, table, pre, blockquote { margin: 0 0 .9em; }
        a { color: #0a64c2; }
        code { font: .92em "Cascadia Mono", Consolas, monospace; background: #f3f3f3; padding: .1em .3em; border-radius: 3px; }
        pre { background: #f6f6f6; padding: .8em 1em; border-radius: 6px; overflow-x: auto; }
        pre code { background: none; padding: 0; }
        pre.plain { background: none; padding: 0; white-space: pre-wrap; font: 10pt/1.45 "Cascadia Mono", Consolas, monospace; }
        blockquote { border-left: 3px solid #d0d0d0; padding-left: 1em; color: #555; }
        table { border-collapse: collapse; } th, td { border: 1px solid #d6d6d6; padding: .35em .7em; } th { background: #f5f5f5; }
        img { max-width: 100%; }
        ul.contains-task-list { list-style: none; padding-left: 1.2em; }
        """;

    private static string Body(string input, Src src) => src switch
    {
        Src.Markdown => Markdown.ToHtml(File.ReadAllText(input), Pipeline),
        _ => $"<pre class=\"plain\">{WebUtility.HtmlEncode(File.ReadAllText(input))}</pre>",
    };

    private static string PageCss(bool a4) => $"@page {{ size: {(a4 ? "A4" : "Letter")}; }}";

    private static string StandaloneHtml(string input, Src src, bool? paper)
    {
        var title = WebUtility.HtmlEncode(Path.GetFileNameWithoutExtension(input));
        var baseHref = new Uri(Path.GetDirectoryName(Path.GetFullPath(input))! + Path.DirectorySeparatorChar).AbsoluteUri;
        var page = paper is { } a4 ? PageCss(a4) : "";
        return $"""
            <!doctype html>
            <html><head><meta charset="utf-8"><title>{title}</title>
            <base href="{baseHref}">
            <style>{Css}{page}</style></head>
            <body>
            {Body(input, src)}
            </body></html>
            """;
    }

    /// <summary>HTML ready to print: paper size set, relative links resolved against the original folder.</summary>
    private static string PrintableHtml(string input, Src src, bool a4)
    {
        if (src != Src.Html) return StandaloneHtml(input, src, a4);
        var html = File.ReadAllText(input);
        var baseHref = new Uri(Path.GetDirectoryName(Path.GetFullPath(input))! + Path.DirectorySeparatorChar).AbsoluteUri;
        var inject = $"<base href=\"{baseHref}\"><style>{PageCss(a4)}</style>";
        var head = HeadTag().Match(html);
        return head.Success ? html.Insert(head.Index + head.Length, inject) : inject + html;
    }

    [GeneratedRegex("<head[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex HeadTag();

    /// <summary>
    /// Prints an HTML file to PDF with WebView2 (part of Windows 11, unlike Edge itself, which can be uninstalled).
    /// WebView2 needs a window and a message loop, so it runs on its own STA thread with a hidden window.
    /// </summary>
    private static Task PrintToPdfAsync(string html, string pdf, bool a4)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.InvokeAsync(async () =>
            {
                HwndSource? window = null;
                CoreWebView2Controller? controller = null;
                try
                {
                    window = new HwndSource(new HwndSourceParameters("Convert PDF") { Width = 900, Height = 1200, WindowStyle = 0 });
                    var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Convert", "WebView2");
                    CoreWebView2Environment env;
                    try { env = await CoreWebView2Environment.CreateAsync(null, data); }
                    catch (WebView2RuntimeNotFoundException) { throw new ConvertException("Creating PDFs needs the Microsoft Edge WebView2 Runtime"); }
                    controller = await env.CreateCoreWebView2ControllerAsync(window.Handle);
                    var web = controller.CoreWebView2;
                    web.Settings.IsScriptEnabled = false;
                    var loaded = new TaskCompletionSource<bool>();
                    web.NavigationCompleted += (_, e) => loaded.TrySetResult(e.IsSuccess);
                    web.Navigate(new Uri(html).AbsoluteUri);
                    if (!await loaded.Task) throw new ConvertException("Couldn't load the document to print it");

                    var print = env.CreatePrintSettings();
                    print.ShouldPrintHeaderAndFooter = false;
                    print.ShouldPrintBackgrounds = true;
                    print.PageWidth = a4 ? 8.27 : 8.5;          // inches
                    print.PageHeight = a4 ? 11.69 : 11;
                    print.MarginTop = print.MarginBottom = print.MarginLeft = print.MarginRight = 0.79;   // 20 mm
                    if (!await web.PrintToPdfAsync(pdf, print) || !File.Exists(pdf))
                        throw new ConvertException("Couldn't create the PDF");
                    done.TrySetResult();
                }
                catch (Exception e)
                {
                    Paths.TryDelete(pdf);
                    done.TrySetException(e is ConvertException ? e : new ConvertException($"Couldn't create the PDF ({e.Message})"));
                }
                finally
                {
                    controller?.Close();
                    window?.Dispose();
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            });
            Dispatcher.Run();
        }) { IsBackground = true, Name = "Convert PDF printer" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    // LibreOffice

    private static async Task<string> LibreOfficeAsync(string feed, Src src, Target t, string original, string tmp)
    {
        var soffice = Tools.LibreOffice ?? throw new ConvertException(LibreOfficeHint);
        var (ext, filter) = t.Id switch
        {
            "pdf-doc" => ("pdf", "pdf:writer_pdf_Export"),
            "docx" => ("docx", "docx:MS Word 2007 XML"),
            "doc" => ("doc", "doc:MS Word 97"),
            "odt" => ("odt", "odt:writer8"),
            "rtf" => ("rtf", "rtf:Rich Text Format"),
            "txt" => ("txt", "txt:Text (encoded):UTF8"),
            "html" => ("html", "html:XHTML Writer File:UTF8"),
            _ => throw new ConvertException($"Can't convert documents to {t.Label}"),
        };
        var outDir = Path.Combine(tmp, "out");
        Directory.CreateDirectory(outDir);
        var args = new List<string>
        {
            "--headless", "--norestore", "--nologo", "--nodefault", "--nolockcheck",
            $"-env:UserInstallation={new Uri(Path.Combine(tmp, "lo-profile")).AbsoluteUri}",
        };
        // Without these, HTML opens in Writer/Web and plain text guesses its encoding.
        if (src == Src.Html) args.Add("--infilter=HTML (StarWriter)");
        if (src == Src.Text) args.Add("--infilter=Text (encoded):UTF8");
        args.AddRange(["--convert-to", filter, "--outdir", outDir, feed]);

        await Task.Run(() => Proc.RunSync(soffice, args, 180_000));
        var made = Directory.EnumerateFiles(outDir, "*." + ext).FirstOrDefault()
                   ?? throw new ConvertException($"LibreOffice couldn't convert this file to {t.Label}");
        var dest = Paths.UniqueOutput(original, t.Ext);
        File.Move(made, dest);
        return dest;
    }
}
