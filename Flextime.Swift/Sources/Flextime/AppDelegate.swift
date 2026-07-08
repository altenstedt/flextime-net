import AppKit

class AppDelegate: NSObject, NSApplicationDelegate {
    private var statusItem: NSStatusItem!
    private var startItem: NSMenuItem!
    private var stopItem: NSMenuItem!

    private let listen = ManagedProcess(
        arguments: ["flextimed", "listen", "-t", "Europe/Stockholm"],
        logFile: logDir.appendingPathComponent("listen.log"),
        windowTitle: "Flextime — Listen"
    )
    private let sync = ManagedProcess(
        arguments: ["flextimed", "sync", "--every", "0.00:20:00"],
        logFile: logDir.appendingPathComponent("sync.log"),
        windowTitle: "Flextime — Sync"
    )

    func applicationDidFinishLaunching(_ notification: Notification) {
        listen.onStopped = { [weak self] in self?.updateMenuState() }
        sync.onStopped  = { [weak self] in self?.updateMenuState() }
        buildStatusItem()
        startAll()
    }

    func applicationWillTerminate(_ notification: Notification) {
        listen.stop()
        sync.stop()
        listen.waitUntilStopped(for: 3)
        sync.waitUntilStopped(for: 3)
    }

    private func buildStatusItem() {
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        if let button = statusItem.button {
            button.image = NSImage(systemSymbolName: "clock.fill", accessibilityDescription: "Flextime")
            button.image?.isTemplate = true
        }

        let menu = NSMenu()
        menu.autoenablesItems = false

        startItem = NSMenuItem(title: "Start", action: #selector(startAll), keyEquivalent: "")
        startItem.target = self
        menu.addItem(startItem)

        stopItem = NSMenuItem(title: "Stop", action: #selector(stopAll), keyEquivalent: "")
        stopItem.target = self
        stopItem.isEnabled = false
        menu.addItem(stopItem)

        menu.addItem(.separator())

        let listenItem = NSMenuItem(title: "Show Listen Output", action: #selector(showListenOutput), keyEquivalent: "")
        listenItem.target = self
        menu.addItem(listenItem)

        let syncItem = NSMenuItem(title: "Show Sync Output", action: #selector(showSyncOutput), keyEquivalent: "")
        syncItem.target = self
        menu.addItem(syncItem)

        menu.addItem(.separator())

        let quitItem = NSMenuItem(title: "Quit", action: #selector(quitApp), keyEquivalent: "q")
        quitItem.target = self
        menu.addItem(quitItem)

        statusItem.menu = menu
    }

    @objc private func startAll() {
        try? FileManager.default.createDirectory(at: logDir, withIntermediateDirectories: true)
        listen.start() // start() is a no-op for a process that is already running
        sync.start()
        updateMenuState()
    }

    @objc private func stopAll() {
        listen.stop()
        sync.stop()
        updateMenuState()
    }

    @objc private func showListenOutput() { listen.showOutput() }
    @objc private func showSyncOutput()   { sync.showOutput() }

    @objc private func quitApp() {
        NSApp.terminate(nil) // applicationWillTerminate stops the children
    }

    private func updateMenuState() {
        startItem.isEnabled = !listen.isRunning || !sync.isRunning
        stopItem.isEnabled  =  listen.isRunning || sync.isRunning
    }
}

private let logDir = FileManager.default.homeDirectoryForCurrentUser
    .appendingPathComponent("Library/Logs/Flextime")
