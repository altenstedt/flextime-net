import Foundation

class ManagedProcess {
    private let arguments: [String]
    private let logFile: URL
    private let windowTitle: String

    private var process: Process?
    private var logHandle: FileHandle?
    private var lineBuffer = Data()
    private var runID = 0

    private var outputWindow: OutputWindowController?
    private var accumulatedLines: [String] = []
    private static let maxAccumulatedLines = 2000

    var onStopped: (() -> Void)?
    var isRunning: Bool { process?.isRunning ?? false }

    init(arguments: [String], logFile: URL, windowTitle: String) {
        self.arguments = arguments
        self.logFile = logFile
        self.windowTitle = windowTitle
    }

    func start() {
        guard !isRunning else { return }
        runID += 1
        let run = runID
        accumulatedLines = []
        lineBuffer = Data()
        try? logHandle?.close()
        FileManager.default.createFile(atPath: logFile.path, contents: nil)
        logHandle = try? FileHandle(forWritingTo: logFile)

        let child = Process()
        child.executableURL = URL(fileURLWithPath: "/usr/bin/env")
        child.arguments = arguments
        // Include common tool install locations in PATH
        var environment = ProcessInfo.processInfo.environment
        environment["PATH"] = "/usr/local/bin:/opt/homebrew/bin:" + (environment["PATH"] ?? "/usr/bin:/bin")
        child.environment = environment

        let pipe = Pipe()
        child.standardOutput = pipe
        child.standardError = pipe
        pipe.fileHandleForReading.readabilityHandler = { [weak self] handle in
            let data = handle.availableData
            if data.isEmpty {
                handle.readabilityHandler = nil // EOF
            }
            DispatchQueue.main.async {
                self?.handleOutput(data, run: run)
            }
        }
        child.terminationHandler = { [weak self] _ in
            DispatchQueue.main.async {
                self?.handleTermination(run: run)
            }
        }

        do {
            try child.run()
        } catch {
            pipe.fileHandleForReading.readabilityHandler = nil
            appendLines(["Failed to start: \(error.localizedDescription)"])
            return
        }
        process = child
    }

    func stop() {
        process?.terminate()
    }

    /// Blocks the calling thread until the process has exited or the timeout elapses.
    func waitUntilStopped(for timeout: TimeInterval) {
        let deadline = Date(timeIntervalSinceNow: timeout)
        while isRunning && Date() < deadline {
            Thread.sleep(forTimeInterval: 0.05)
        }
    }

    func showOutput() {
        if outputWindow == nil {
            let window = OutputWindowController(title: windowTitle)
            window.appendLines(accumulatedLines)
            outputWindow = window
        }
        outputWindow?.show()
    }

    // MARK: - Output handling (main thread only)

    private func handleOutput(_ data: Data, run: Int) {
        guard run == runID else { return } // stale pipe from a previous run
        guard !data.isEmpty else {
            // EOF: flush any trailing partial line and release the log file
            if !lineBuffer.isEmpty {
                appendLines([decodeLine(lineBuffer)])
                lineBuffer = Data()
            }
            try? logHandle?.close()
            logHandle = nil
            return
        }
        try? logHandle?.write(contentsOf: data)
        lineBuffer.append(data)

        var lines = [String]()
        let newline = Data([0x0A])
        while let range = lineBuffer.range(of: newline) {
            lines.append(decodeLine(lineBuffer[..<range.lowerBound]))
            lineBuffer.removeSubrange(..<range.upperBound)
        }
        appendLines(lines)
    }

    private func handleTermination(run: Int) {
        guard run == runID else { return }
        process = nil
        onStopped?()
    }

    private func decodeLine(_ data: Data) -> String {
        var line = String(data: data, encoding: .utf8) ?? ""
        if line.hasSuffix("\r") {
            line.removeLast()
        }
        return line
    }

    private func appendLines(_ lines: [String]) {
        guard !lines.isEmpty else { return }
        accumulatedLines.append(contentsOf: lines)
        if accumulatedLines.count > Self.maxAccumulatedLines {
            accumulatedLines.removeFirst(accumulatedLines.count - Self.maxAccumulatedLines)
        }
        outputWindow?.appendLines(lines)
    }
}
