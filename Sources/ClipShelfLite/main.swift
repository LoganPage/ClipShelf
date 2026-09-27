import Darwin

if let exitCode = SelfTestCommand.runIfRequested() {
    exit(exitCode)
}

if let exitCode = RuntimeControlCommand.runIfRequested() {
    exit(exitCode)
}

ClipShelfApp.main()
