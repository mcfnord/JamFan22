# JamFan22 Project Instructions

## Running the Application
This application is run and managed as a systemd service. 
- **Do not** use `dotnet run` directly.
- **Do** use `systemctl restart jamfan22` to apply changes and restart the application.
- **Do** use `systemctl status jamfan22` to check the status.

## Testing a Branch Live
To test changes on a branch safely while the production instance runs undisturbed:
1. Execute `./deploy-test-build.sh` from the project root.
2. The script compiles the current code into a sandboxed directory (`/tmp/jamfan-test-build`).
3. It takes a read-only snapshot of production data and copies it to the sandbox.
4. It launches the test instance on port `5000` (e.g., `http://localhost:5000`), leaving all production data completely untouched.

You can stop the test server at any time by pressing `Ctrl+C`.