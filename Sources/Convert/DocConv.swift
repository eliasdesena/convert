import AppKit
import CoreGraphics
import Foundation
import PDFKit

enum DocConv {
    /// Formats macOS's textutil reads natively; Markdown is rendered to HTML first,
    /// anything else (source code, logs…) goes in as plain text.
    static let textutilInputs: Set<String> = ["txt", "text", "rtf", "rtfd", "html", "htm", "doc", "docx", "odt", "wordml", "webarchive", "xml"]

    static func isPlain(_ url: URL) -> Bool {
        let ext = url.pathExtension.lowercased()
        return ext == "txt" || ext == "text" || !textutilInputs.contains(ext)
    }

    /// Markdown becomes a styled HTML file in a temp folder; everything else is used as is.
    private static func source(for input: URL) throws -> (url: URL, cleanup: () -> Void) {
        guard Markdown.isMarkdown(input) else { return (input, {}) }
        let html = try Markdown.renderToTempHTML(input)
        return (html, { try? FileManager.default.removeItem(at: html.deletingLastPathComponent()) })
    }

    static func textutil(_ input: URL, _ t: Target) throws -> URL {
        let out = uniqueOutput(for: input, ext: t.ext)
        let src = try source(for: input)
        defer { src.cleanup() }
        if Markdown.isMarkdown(input), t.ext == "html" {
            try FileManager.default.copyItem(at: src.url, to: out)
            return out
        }
        var args = ["-convert", t.ext]
        if !textutilInputs.contains(src.url.pathExtension.lowercased()) {
            args += ["-format", "txt", "-inputencoding", "UTF-8"]
        }
        if t.ext == "txt" || t.ext == "html" { args += ["-encoding", "UTF-8"] }
        args += ["-output", out.path, src.url.path]
        try runSync("/usr/bin/textutil", args)
        return out
    }

    /// Lays the document out on paper and prints it to PDF. Must run on the main thread (AppKit text system).
    @MainActor
    static func toPDF(_ original: URL, _ out: URL, a4: Bool) throws {
        let src = try source(for: original)
        defer { src.cleanup() }
        let input = src.url
        var opts: [NSAttributedString.DocumentReadingOptionKey: Any] = [:]
        if isPlain(input) {
            opts[.documentType] = NSAttributedString.DocumentType.plain
            opts[.characterEncoding] = String.Encoding.utf8.rawValue
        }
        let loaded = try NSAttributedString(url: input, options: opts, documentAttributes: nil)
        let text = NSMutableAttributedString(attributedString: loaded)
        let full = NSRange(location: 0, length: text.length)
        // Text without an explicit colour would otherwise follow dark mode and print white.
        text.enumerateAttribute(.foregroundColor, in: full) { value, range, _ in
            if value == nil { text.addAttribute(.foregroundColor, value: NSColor.black, range: range) }
        }
        if isPlain(input) {
            text.enumerateAttribute(.font, in: full) { value, range, _ in
                if value == nil { text.addAttribute(.font, value: NSFont.systemFont(ofSize: 11), range: range) }
            }
        }

        let info = NSPrintInfo()
        info.paperSize = a4 ? NSSize(width: 595.28, height: 841.89) : NSSize(width: 612, height: 792)
        info.topMargin = 56; info.bottomMargin = 56; info.leftMargin = 56; info.rightMargin = 56
        info.horizontalPagination = .fit
        info.verticalPagination = .automatic
        info.isVerticallyCentered = false
        info.jobDisposition = .save
        info.dictionary()[NSPrintInfo.AttributeKey.jobSavingURL] = out

        let width = info.paperSize.width - info.leftMargin - info.rightMargin
        let view = NSTextView(frame: NSRect(x: 0, y: 0, width: width, height: 100))
        view.drawsBackground = false
        view.textContainerInset = .zero
        view.textContainer?.lineFragmentPadding = 0
        view.textStorage?.setAttributedString(text)
        if let lm = view.layoutManager, let tc = view.textContainer {
            lm.ensureLayout(for: tc)
            view.frame.size.height = max(100, lm.usedRect(for: tc).height)
        }

        let op = NSPrintOperation(view: view, printInfo: info)
        op.showsPrintPanel = false
        op.showsProgressPanel = false
        guard op.run(), FileManager.default.fileExists(atPath: out.path) else {
            throw ConvertError("Couldn't create the PDF")
        }
    }
}

enum PDFConv {
    static func convert(_ input: URL, _ t: Target, _ s: ConvSettings,
                        progress: @Sendable (Double) -> Void) throws -> [URL] {
        if t.id == "pdf-txt" {
            guard let doc = PDFDocument(url: input) else { throw ConvertError("Couldn't open this PDF") }
            if doc.isLocked { throw ConvertError("This PDF is password-protected") }
            guard let text = doc.string, text.rangeOfCharacter(from: CharacterSet.whitespacesAndNewlines.inverted) != nil else {
                throw ConvertError("This PDF contains no extractable text")
            }
            let out = uniqueOutput(for: input, ext: "txt")
            try text.write(to: out, atomically: true, encoding: .utf8)
            return [out]
        }

        guard let doc = CGPDFDocument(input as CFURL) else { throw ConvertError("Couldn't open this PDF") }
        if doc.isEncrypted && !doc.isUnlocked { throw ConvertError("This PDF is password-protected") }
        let count = doc.numberOfPages
        guard count > 0 else { throw ConvertError("This PDF has no pages") }

        // One page → file next to the PDF. Several → a folder with page 1, page 2, …
        var folder: URL?
        if count > 1 {
            let f = uniqueFolder(for: input, suffix: " pages")
            try FileManager.default.createDirectory(at: f, withIntermediateDirectories: true)
            folder = f
        }
        let single = uniqueOutput(for: input, ext: t.ext)
        let scale = CGFloat(s.pdfDPI) / 72
        let type = ImageConv.utType(t.id)
        var props: [CFString: Any] = [:]
        if t.id == "pdf-jpg" { props[kCGImageDestinationLossyCompressionQuality] = s.imageQuality }

        for i in 1...count {
            guard let page = doc.page(at: i) else { continue }
            let box = page.getBoxRect(.cropBox)
            let turned = page.rotationAngle % 180 != 0
            let size = turned ? CGSize(width: box.height, height: box.width) : box.size
            let pw = Int((size.width * scale).rounded()), ph = Int((size.height * scale).rounded())
            guard let ctx = CGContext(data: nil, width: pw, height: ph, bitsPerComponent: 8, bytesPerRow: 0,
                                      space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                      bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue)
            else { throw ConvertError("Page \(i) is too large to render") }
            ctx.setFillColor(CGColor(red: 1, green: 1, blue: 1, alpha: 1))
            ctx.fill(CGRect(x: 0, y: 0, width: pw, height: ph))
            ctx.interpolationQuality = .high
            ctx.scaleBy(x: scale, y: scale)
            ctx.concatenate(page.getDrawingTransform(.cropBox, rect: CGRect(origin: .zero, size: size),
                                                     rotate: 0, preserveAspectRatio: true))
            ctx.drawPDFPage(page)
            guard let img = ctx.makeImage() else { throw ConvertError("Couldn't render page \(i)") }

            let out = folder.map { $0.appendingPathComponent("page \(i)").appendingPathExtension(t.ext) } ?? single
            try ImageConv.write(img, to: out, type: type, label: t.label, props: props)
            progress(Double(i) / Double(count))
        }
        return [folder ?? single]
    }
}
