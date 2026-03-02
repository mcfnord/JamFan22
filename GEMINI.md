# JamFan22 Project Instructions

## Development Workflow
- **All code changes** must occur in a non-main branch.
- **Never modify files** on the `main` branch directly.

## Running the Application
This application is run and managed as a systemd service named jamfan22.
- **Do not** use `dotnet run` directly.

## Testing a Branch Live
To test changes on a branch safely while the production instance runs undisturbed:
1. Execute `./deploy-test-build.sh` from the project root.
2. The script compiles the current code into a sandboxed directory (`/tmp/jamfan-test-build`).
3. It takes a read-only snapshot of production data and copies it to the sandbox.
4. It launches the test instance on port `5000` (e.g., `http://localhost:5000`), leaving all production data completely untouched.

## Data File Handling & Token Efficiency
- **NEVER** read entire `.json`, `.csv`, `.log`, or other large data files into context using `read_file` without explicit bounds.
- **Always** use `grep_search` to find specific keys, values, or lines of interest within these files.
- If sequential reading is strictly required, use `read_file` with strict `limit` (e.g., 50 lines) and `offset` parameters to paginate through the file.
- Prioritize using terminal utilities (like `head`, `tail`, `jq`, or `awk` via `run_shell_command` with `--no-pager`) if complex data extraction or summarization is needed before bringing data into the context window.
