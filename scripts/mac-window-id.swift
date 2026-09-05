// Print the CGWindowID(s) of Perch's main window(s), for `screencapture -l`.
// With a PID argument, only that process's windows — so a harness shoots the
// instance it launched and never the user's own Perch.app.
//   swift scripts/mac-window-id.swift [pid]
import CoreGraphics
import Foundation

let wantPid: Int? = CommandLine.arguments.count > 1 ? Int(CommandLine.arguments[1]) : nil
let opts: CGWindowListOption = [.optionOnScreenOnly, .excludeDesktopElements]
guard let list = CGWindowListCopyWindowInfo(opts, kCGNullWindowID) as? [[String: Any]] else { exit(1) }
for w in list {
    let owner = w[kCGWindowOwnerName as String] as? String ?? ""
    let pid = w[kCGWindowOwnerPID as String] as? Int ?? -1
    let num = w[kCGWindowNumber as String] as? Int ?? 0
    let layer = w[kCGWindowLayer as String] as? Int ?? -1
    if owner.lowercased() == "perch" && layer == 0 && (wantPid == nil || wantPid == pid) { print(num) }
}
