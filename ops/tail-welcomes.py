#!/usr/bin/env python3
"""
tail-welcomes.py — live tail of JamFan22 welcome messages.
Non-English messages are translated to English via Gemini.
Shows key context signals so you can judge whether the LLM used them well.
Run from anywhere: python3 /root/JamFan22/tail-welcomes.py
"""
import sys, time, re, json, urllib.request, html, os

BASE = "/root/JamFan22/JamFan22"
LOG_PATH = f"{BASE}/data/welcome-llm.log"
KEY_PATH = f"{BASE}/data/gemini-key.txt"

SEP = re.compile(
    r'^--- (?:(\[GROUP\]) )?(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2} UTC)\s+'
    r'(?:nation=(\w+)\s+)?ms=\d+ ---$'
)

# Lines to skip from context display (already shown or boilerplate lore)
SKIP_PREFIXES = (
    "CONTEXT:",
    "Server: ",
    "Server identity:",
    "Server themes:",
    "Stream slot:",
    "Language to use for message:",
    "Arriving musician:",
    "Other players on server",
    "MESSAGE:",
)

def load_key():
    try:
        return open(KEY_PATH).read().strip()
    except Exception:
        return None

GEMINI_KEY = load_key()

def strip_html(s):
    return html.unescape(re.sub(r'<[^>]+>', '', s)).strip()

def translate_to_english(text):
    if not GEMINI_KEY:
        return "[no API key]"
    url = (
        "https://generativelanguage.googleapis.com/v1beta/models/"
        f"gemini-2.0-flash:generateContent?key={GEMINI_KEY}"
    )
    payload = {
        "contents": [{"parts": [{"text": f"Translate to English. Return ONLY the translation:\n{text}"}]}]
    }
    req = urllib.request.Request(
        url, data=json.dumps(payload).encode(),
        headers={"Content-Type": "application/json"}
    )
    with urllib.request.urlopen(req, timeout=8) as r:
        data = json.loads(r.read())
    return data["candidates"][0]["content"]["parts"][0]["text"].strip()

def is_signal_line(line):
    """Return True for context lines that carry interesting info for the LLM."""
    if not line.strip():
        return False
    if any(line.startswith(p) for p in SKIP_PREFIXES):
        return False
    # Skip "  - Name (instrument, country, N min ...)" room-member detail lines
    # but keep them — they're useful for tutoring
    return True

def process_entry(lines):
    if not lines:
        return
    m = SEP.match(lines[0])
    if not m:
        return

    group_flag = m.group(1)   # "[GROUP]" or None
    timestamp  = m.group(2)
    nation     = m.group(3) or "??"

    server = arriving = language = None
    message_lines = []
    signal_lines = []
    in_message = False

    for line in lines[1:]:
        if line.startswith("Server: "):
            server = line[8:]
        elif line.startswith("Arriving musician: "):
            arriving = line[19:]
        elif line.startswith("Language to use for message: "):
            language = line[29:]
        elif line == "MESSAGE:":
            in_message = True
        elif in_message:
            message_lines.append(line)
        elif is_signal_line(line):
            signal_lines.append(line)

    raw_message = " ".join(message_lines).strip()
    if not raw_message:
        return

    clean = strip_html(raw_message)
    if not clean:
        return

    # Translate if non-English
    translated = None
    if language and language.lower() != "english":
        try:
            translated = translate_to_english(clean)
        except Exception as e:
            translated = f"[translation failed: {e}]"

    # ── Print ──────────────────────────────────────────────────────────────
    tag = "\U0001f4e2 GROUP  " if group_flag else "       "
    print(f"\n{'─'*64}")
    print(f"{tag}{timestamp}  [{nation}]  {server or '?'}")
    if arriving:
        print(f"  → {arriving}")

    # Context signals (indented, dimmed with a bullet)
    if signal_lines:
        print()
        for sig in signal_lines:
            print(f"    · {sig.strip()}")

    # The message
    print()
    if translated:
        print(f"  {language}: {clean}")
        print(f"  English:  {translated}")
    else:
        print(f"  {clean}")
    sys.stdout.flush()

def tail_log():
    print(f"Watching {LOG_PATH}")
    print("Waiting for new welcome messages — Ctrl-C to stop.\n")
    sys.stdout.flush()

    with open(LOG_PATH, "r") as f:
        f.seek(0, 2)  # jump to end
        buf = []
        has_message_content = False

        while True:
            line = f.readline()
            if not line:
                time.sleep(0.4)
                continue

            line = line.rstrip("\n")
            if SEP.match(line):
                # New entry — flush whatever was buffered
                if buf:
                    process_entry(buf)
                buf = [line]
                has_message_content = False
            else:
                buf.append(line)
                if line.startswith("<p") or line.startswith("<P"):
                    has_message_content = True
                # Flush as soon as the blank line after the message arrives
                elif has_message_content and line == "":
                    process_entry(buf)
                    buf = []
                    has_message_content = False

if __name__ == "__main__":
    try:
        tail_log()
    except KeyboardInterrupt:
        print("\nStopped.")
