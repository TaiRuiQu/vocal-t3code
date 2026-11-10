# T3 Voice Host

Terminal backend for T3 Voice. Run `t3` to sign in to T3 Connect and print the machine's HTTPS address once its tunnel is reachable. Keep the terminal open while connected.

Only OpenCode, Codex, and Claude Code are registered. Provider CLIs must be installed and authenticated on this machine. No web client or Electron shell is built or served.

Run `t3 serve --base-dir <directory>` for unattended startup with saved authorization. See the repository README for development and packaging.
