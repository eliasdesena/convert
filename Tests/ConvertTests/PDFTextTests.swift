import CoreGraphics
import CoreText
import XCTest
@testable import Convert

final class PDFTextTests: XCTestCase {
    private func makePDF(text: String? = nil) throws -> URL {
        let files = FileManager.default
        let dir = files.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try files.createDirectory(at: dir, withIntermediateDirectories: false)
        addTeardownBlock { try files.removeItem(at: dir) }

        let url = dir.appendingPathComponent("sample.pdf")
        var bounds = CGRect(x: 0, y: 0, width: 200, height: 200)
        let context = try XCTUnwrap(CGContext(url as CFURL, mediaBox: &bounds, nil))
        context.beginPDFPage(nil)
        context.fill(CGRect(x: 10, y: 10, width: 20, height: 20))
        if let text {
            context.textPosition = CGPoint(x: 20, y: 100)
            CTLineDraw(CTLineCreateWithAttributedString(NSAttributedString(string: text)), context)
        }
        context.endPDFPage()
        context.closePDF()
        return url
    }

    func testGraphicsOnlyPDFDoesNotCreateTextFile() throws {
        let input = try makePDF()
        let target = try XCTUnwrap(Targets.byID("pdf-txt"))
        XCTAssertThrowsError(try PDFConv.convert(input, target, ConvSettings()) { _ in }) { error in
            XCTAssertEqual(error.localizedDescription, "This PDF contains no extractable text")
        }
        let output = input.deletingPathExtension().appendingPathExtension("txt")
        XCTAssertFalse(FileManager.default.fileExists(atPath: output.path))
    }

    func testExportsSelectableText() throws {
        let input = try makePDF(text: "Sample text")
        let target = try XCTUnwrap(Targets.byID("pdf-txt"))
        let outputs = try PDFConv.convert(input, target, ConvSettings()) { _ in }
        let text = try String(contentsOf: XCTUnwrap(outputs.first), encoding: .utf8)
        XCTAssertEqual(text.trimmingCharacters(in: .whitespacesAndNewlines), "Sample text")
    }
}
