import Darwin

if let exitCode = SelfTestCommand.runIfRequested() {
    exit(exitCode)
}

ClipShelfApp.main()
