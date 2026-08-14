import XCTest
@testable import ClipSync

final class AppIdentityTests: XCTestCase {

    func testEqualityIsOnKindAndKeyOnly() {
        let a = AppIdentity(kind: .bundle, key: "com.example.app", displayName: "Example",
                            path: "/Applications/Example.app")!
        let b = AppIdentity(kind: .bundle, key: "com.example.app", displayName: "Renamed",
                            path: "/somewhere/else/Example.app")!
        XCTAssertEqual(a, b)
        XCTAssertEqual(a.hashValue, b.hashValue)
    }

    func testKeyMatchingIsCaseInsensitive() {
        let a = AppIdentity(kind: .bundle, key: "com.Example.APP", displayName: "x")!
        let b = AppIdentity(kind: .bundle, key: "com.example.app", displayName: "x")!
        XCTAssertEqual(a, b)
        XCTAssertEqual(a.key, "com.example.app")
    }

    func testKeyIsTrimmed() {
        let a = AppIdentity(kind: .bundle, key: "  com.example.app \n", displayName: "x")!
        XCTAssertEqual(a.key, "com.example.app")
    }

    func testBlankKeyIsRejected() {
        XCTAssertNil(AppIdentity(kind: .bundle, key: "", displayName: "x"))
        XCTAssertNil(AppIdentity(kind: .bundle, key: "   ", displayName: "x"))
    }

    func testDifferentKeysDiffer() {
        let a = AppIdentity(kind: .bundle, key: "com.example.one", displayName: "x")!
        let b = AppIdentity(kind: .bundle, key: "com.example.two", displayName: "x")!
        XCTAssertNotEqual(a, b)
    }
}
