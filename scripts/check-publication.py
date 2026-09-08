"""Read-only checks of the files that would enter a public source commit."""

from pathlib import Path
import re
import subprocess
import sys
from urllib.parse import unquote, urlsplit


ROOT = Path(__file__).resolve().parent.parent
FORBIDDEN_EXTENSIONS = {
    '.iso', '.gcm', '.rvz', '.gcz', '.wbfs', '.wad', '.dll', '.exe', '.pdb',
    '.zip', '.7z', '.tasproj', '.tasstate', '.tasreplay', '.dtm', '.dmw',
    '.sav', '.raw', '.sqlite', '.db', '.vsix', '.log', '.pfx', '.p12', '.pem', '.pyc',
}
FORBIDDEN_FOLDERS = {'bin', 'obj', 'artifacts', '.runs', '.local', 'node_modules', 'roms', '__pycache__'}
PATTERNS = {
    'private key': re.compile(r'-----BEGIN (?:RSA |EC |OPENSSH |DSA )?PRIVATE KEY-----'),
    'GitHub credential': re.compile(r'\b(?:gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{40,})\b'),
    'AWS access key': re.compile(r'\b(?:AKIA|ASIA)[A-Z0-9]{16}\b'),
    'API credential': re.compile(r'\bsk-(?:proj-|svcacct-)?[A-Za-z0-9_-]{32,}\b'),
    'personal Windows path': re.compile(r'(?i)\b[A-Z]:[/\\]+Users[/\\]+(?!Public\b|<|YOUR_USERNAME\b)[^\s/\\`"\']+'),
    'local development path': re.compile(r'(?i)\bE:[/\\]+Dev[/\\]+'),
}
LINK = re.compile(r'!?\[[^\]\n]*\]\((<[^>]+>|[^\s)]+)(?:\s+["\'][^\n]*["\'])?\)')


def main():
    raw = subprocess.check_output(
        ['git', 'ls-files', '--cached', '--others', '--exclude-standard', '-z'], cwd=ROOT)
    names = sorted(set(raw.decode('utf-8').rstrip('\0').split('\0')) - {''})
    # Deleted tracked files do not enter the next commit.
    candidates = {name for name in names if (ROOT / name).is_file()}
    issues = []
    total = 0
    for name in sorted(candidates):
        file = ROOT / name
        parts = Path(name).parts
        suffix = file.suffix.lower()
        if (suffix in FORBIDDEN_EXTENSIONS or any(p.lower() in FORBIDDEN_FOLDERS for p in parts)
                or name.startswith('native/dolphin-libretro/') or name.startswith('native/build')
                or (file.name.startswith('.env') and file.name != '.env.example')
                or re.search(r'\.(sqlite|db)-(wal|shm|journal)$', name)):
            issues.append(f'{name}: generated, private, or runtime artifact')
        size = file.stat().st_size
        total += size
        if size > 10 * 1024 * 1024:
            issues.append(f'{name}: exceeds 10 MiB source-file review limit')
        data = file.read_bytes()
        if b'\0' in data:
            if suffix not in {'.png', '.ico', '.jpg', '.jpeg', '.woff', '.woff2'}:
                issues.append(f'{name}: unexpected binary file')
            continue
        try:
            text = data.decode('utf-8-sig')
        except UnicodeDecodeError:
            issues.append(f'{name}: non-UTF-8 file needs manual review')
            continue
        for label, pattern in PATTERNS.items():
            for match in pattern.finditer(text):
                line = text.count('\n', 0, match.start()) + 1
                issues.append(f'{name}:{line}: {label}')
        if suffix != '.md':
            continue
        # Ignore illustrative Markdown inside fenced code blocks.
        prose = re.sub(r'(?ms)^```[^\n]*\n.*?^```[^\n]*$', '', text)
        for match in LINK.finditer(prose):
            target = match.group(1).strip('<>')
            parsed = urlsplit(target)
            if parsed.scheme or parsed.netloc or not parsed.path:
                continue
            destination = (file.parent / unquote(parsed.path)).resolve()
            try:
                relative = destination.relative_to(ROOT).as_posix()
            except ValueError:
                issues.append(f'{name}: Markdown link leaves repository')
                continue
            if relative not in candidates and not any(n.startswith(relative.rstrip('/') + '/') for n in candidates):
                issues.append(f'{name}: broken or unshipped Markdown link to {relative}')
    if issues:
        print('\n'.join(issues))
        print(f'FAIL: {len(issues)} finding(s) in {len(candidates)} candidate files.')
        return 1
    print(f'PASS: {len(candidates)} candidate files ({total / 1024 / 1024:.2f} MiB); no publication findings.')
    print('This is a heuristic source-tree check, not a Git-history or exhaustive secret audit.')
    return 0


if __name__ == '__main__':
    sys.exit(main())
