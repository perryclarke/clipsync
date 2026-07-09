import AppKit
import Foundation

// clipfuzz [count]
//
// Writes [count] clipboard items (default 20) at random intervals (3-20s).
// Each item is "<machine> <seq> <lorem ipsum>" where the lorem portion
// is 10-4000 chars, weighted so a length below 100 is 3x as likely as
// 100+. The machine name and sequence number make missed transfers
// easy to spot when correlating two peers' transfers.log files.

let words = [
    "lorem","ipsum","dolor","sit","amet","consectetur","adipiscing","elit",
    "sed","do","eiusmod","tempor","incididunt","ut","labore","et","dolore",
    "magna","aliqua","enim","ad","minim","veniam","quis","nostrud",
    "exercitation","ullamco","laboris","nisi","aliquip","ex","ea","commodo",
    "consequat","duis","aute","irure","in","reprehenderit","voluptate",
    "velit","esse","cillum","eu","fugiat","nulla","pariatur","excepteur",
    "sint","occaecat","cupidatat","non","proident","sunt","culpa","qui",
    "officia","deserunt","mollit","anim","id","est","laborum"
]

func randomString(length: Int) -> String {
    var out = ""
    while out.count < length {
        if !out.isEmpty { out += " " }
        out += words.randomElement()!
    }
    return String(out.prefix(length))
}

/// Lorem length: low band [10..99] is 3x as likely as high band [100..4000].
func randomLoremLength() -> Int {
    Int.random(in: 0..<4) == 0
        ? Int.random(in: 100...4000)
        : Int.random(in: 10...99)
}

let count: Int = {
    if CommandLine.arguments.count < 2 { return 20 }
    if let n = Int(CommandLine.arguments[1]), n > 0 { return n }
    FileHandle.standardError.write(Data("usage: clipfuzz [count]\n".utf8))
    exit(2)
}()

let machine = Host.current().localizedName ?? ProcessInfo.processInfo.hostName
let pb = NSPasteboard.general
let formatter = ISO8601DateFormatter()
formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]

for seq in 1...count {
    let lorem = randomString(length: randomLoremLength())
    let s = "\(machine) \(seq) \(lorem)"
    pb.clearContents()
    pb.setString(s, forType: .string)
    let ts = formatter.string(from: Date())
    let preview = s.prefix(60).replacingOccurrences(of: "\n", with: "\\n")
    print("\(ts) WRITE \(seq)/\(count) len=\(s.utf8.count) \"\(preview)\"")
    fflush(stdout)
    if seq < count {
        let delay = Double.random(in: 3...20)
        Thread.sleep(forTimeInterval: delay)
    }
}
