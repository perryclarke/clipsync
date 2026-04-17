// swift-tools-version:6.0
import PackageDescription

let package = Package(
    name: "ClipSync",
    platforms: [.macOS(.v15)],
    products: [
        .executable(name: "ClipSync", targets: ["ClipSync"])
    ],
    dependencies: [
        .package(url: "https://github.com/apple/swift-crypto.git", from: "3.8.0"),
        .package(url: "https://github.com/apple/swift-certificates.git", from: "1.0.0"),
        .package(url: "https://github.com/apple/swift-asn1.git", from: "1.0.0"),
        .package(url: "https://github.com/valpackett/SwiftCBOR.git", from: "0.5.0")
    ],
    targets: [
        .executableTarget(
            name: "ClipSync",
            dependencies: [
                .product(name: "Crypto", package: "swift-crypto"),
                .product(name: "X509", package: "swift-certificates"),
                .product(name: "SwiftASN1", package: "swift-asn1"),
                .product(name: "SwiftCBOR", package: "SwiftCBOR")
            ],
            path: "Sources/ClipSync",
            swiftSettings: [.swiftLanguageMode(.v5)]
        )
    ]
)
