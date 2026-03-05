# JamFan22 Project Instructions

## Development Workflow
- **All code changes** must occur in a non-main branch.
- **Never modify files** on the `main` branch directly.

## Running the Application
This application is run and managed as a systemd service named jamfan22.
- **Do not** use `dotnet run` directly.

## Data File Handling & Token Efficiency
- **NEVER** read entire `.json`, `.csv`, `.log`, or other large data files into context using `read_file` without explicit bounds.
- **Always** use `grep_search` to find specific keys, values, or lines of interest within these files.
- If sequential reading is strictly required, use `read_file` with strict `limit` (e.g., 50 lines) and `offset` parameters to paginate through the file.
- Prioritize using terminal utilities (like `head`, `tail`, `jq`, or `awk` via `run_shell_command` with `--no-pager`) if complex data extraction or summarization is needed before bringing data into the context window.
