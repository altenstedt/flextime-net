// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "Flextime",
    platforms: [.macOS(.v13)],
    targets: [
        .executableTarget(
            name: "Flextime",
            path: "Sources/Flextime"
        )
    ]
)
