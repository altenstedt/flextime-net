import AppKit

class OutputWindowController: NSObject, NSWindowDelegate {
    private var window: NSWindow!
    private var textView: NSTextView!

    private static let font      = NSFont.monospacedSystemFont(ofSize: 12, weight: .regular)
    private static let textColor = NSColor(red: 0.83, green: 0.83, blue: 0.83, alpha: 1.0)
    private static let bgColor   = NSColor(red: 0.12, green: 0.12, blue: 0.12, alpha: 1.0)
    private static let maxLength = 400_000 // characters of scrollback kept in the view

    init(title: String) {
        super.init()
        buildWindow(title: title)
    }

    private func buildWindow(title: String) {
        window = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 800, height: 500),
            styleMask: [.titled, .closable, .miniaturizable, .resizable],
            backing: .buffered,
            defer: false
        )
        window.title = title
        window.delegate = self
        window.center()

        let scrollView = NSTextView.scrollableTextView()
        scrollView.frame = window.contentView!.bounds
        scrollView.autoresizingMask = [.width, .height]

        textView = scrollView.documentView as? NSTextView
        textView.isEditable = false
        textView.isSelectable = true
        textView.font = Self.font
        textView.backgroundColor = Self.bgColor
        textView.textColor = Self.textColor

        window.contentView?.addSubview(scrollView)
    }

    func show() {
        window.orderFrontRegardless()
    }

    // Must be called on the main thread
    func appendLines(_ lines: [String]) {
        guard !lines.isEmpty, let storage = textView.textStorage else { return }
        let attrs: [NSAttributedString.Key: Any] = [
            .font: Self.font,
            .foregroundColor: Self.textColor
        ]
        storage.append(NSAttributedString(string: lines.joined(separator: "\n") + "\n", attributes: attrs))
        if storage.length > Self.maxLength {
            storage.deleteCharacters(in: NSRange(location: 0, length: storage.length - Self.maxLength))
        }
        textView.scrollToEndOfDocument(nil)
    }

    // MARK: - NSWindowDelegate

    func windowShouldClose(_ sender: NSWindow) -> Bool {
        window.orderOut(nil) // hide rather than destroy
        return false
    }
}
