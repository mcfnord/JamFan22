import os
import base64
import sys

# Configuration
EXTENSIONS = {'.cshtml', '.cs', '.csproj', '.sln', '.md', '.html', '.css'}
IGNORE_DIRS = {'bin', 'obj', '.git', '.vs', '.idea', 'lib'}

def copy_to_clipboard_osc52(text):
    """Sends a special ANSI escape sequence to tell your local terminal to copy the text."""
    # OSC 52 has a limit in some terminals (often around 100k chars), 
    # but Windows Terminal is quite generous.
    encoded = base64.b64encode(text.encode('utf-8')).decode('utf-8')
    sys.stdout.write(f"\033]52;c;{encoded}\a")
    sys.stdout.flush()

def bundle_code():
    output = []
    root_dir = os.getcwd()
    
    output.append(f"Project Context for: {os.path.basename(root_dir)}\n")
    
    for root, dirs, files in os.walk(root_dir):
        # Modify dirs in-place to skip ignored directories
        dirs[:] = [d for d in dirs if d not in IGNORE_DIRS]
        
        for file in files:
            ext = os.path.splitext(file)[1]
            if ext in EXTENSIONS:
                file_path = os.path.join(root, file)
                rel_path = os.path.relpath(file_path, root_dir)
                
                try:
                    with open(file_path, 'r', encoding='utf-8') as f:
                        content = f.read()
                        output.append(f"\n--- START FILE: {rel_path} ---\n")
                        output.append(content)
                        output.append(f"\n--- END FILE: {rel_path} ---\n")
                except Exception as e:
                    print(f"Skipping {rel_path}: {e}")

    full_text = "".join(output)
    
    # Try to push to your local clipboard over SSH
    copy_to_clipboard_osc52(full_text)
    
    # Also save it to a file as a backup
    output_file = "gemini-paste.txt"
    with open(output_file, 'w', encoding='utf-8') as f:
        f.write(full_text)
        
    print(f"Bundled {len(full_text)} characters from project.")
    print(f"1. Saved to {output_file}")
    print("2. Attempted to push to your LOCAL clipboard via OSC 52!")
    print("\nIf it worked, you can now just press Ctrl+V in your browser.")

if __name__ == "__main__":
    bundle_code()
