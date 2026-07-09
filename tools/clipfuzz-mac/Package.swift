// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "clipfuzz",
    platforms: [.macOS(.v13)],
    products: [
        .executable(name: "clipfuzz", targets: ["clipfuzz"])
    ],
    dependencies: [],
    targets: [
        .executableTarget(
            name: "clipfuzz",
            dependencies: [],
            path: "Sources/clipfuzz"
        )
    ]
)
