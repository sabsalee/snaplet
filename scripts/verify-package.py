"""Static payload verification. Does not claim Windows native execution success."""
import hashlib
import json
from pathlib import Path
import struct
import sys

root = Path(__file__).resolve().parents[1]
package = Path(sys.argv[1]) if len(sys.argv) > 1 else root / 'artifacts/snaplet-win-x64'
required = ['Snaplet.exe', 'Snaplet.dll', 'Capture.Core.dll', 'Snaplet.runtimeconfig.json',
            'Tesseract.dll', 'Velopack.dll', 'licenses/Velopack-1.2.161-LICENSE.txt', 'x64/leptonica-1.82.0.dll', 'x64/tesseract50.dll',
            'tessdata/kor.traineddata', 'tessdata/eng.traineddata', 'tessdata/LICENSE',
            'licenses/Tesseract.NET-5.2.0-LICENSE.txt', 'licenses/Tesseract-5.2.0-LICENSE.txt',
            'licenses/Leptonica-1.82.0-LICENSE.txt', 'START-HERE.ko.md', 'THIRD-PARTY-NOTICES.md']
for name in required:
    assert (package / name).is_file(), f'Missing: {name}'
for name in ['Snaplet.exe', 'x64/leptonica-1.82.0.dll', 'x64/tesseract50.dll']:
    data = (package / name).read_bytes()
    assert data[:2] == b'MZ', f'Not PE: {name}'
    offset = struct.unpack_from('<I', data, 0x3c)[0]
    assert data[offset:offset+4] == b'PE\0\0', f'Bad PE: {name}'
    assert struct.unpack_from('<H', data, offset+4)[0] == 0x8664, f'Not x64: {name}'
for name in ['eng.traineddata', 'kor.traineddata']:
    actual = hashlib.sha256((package / 'tessdata' / name).read_bytes()).hexdigest()
    expected = hashlib.sha256((root / 'assets/tessdata' / name).read_bytes()).hexdigest()
    assert actual == expected, f'Altered model: {name}'
files = {str(path.relative_to(package)): hashlib.sha256(path.read_bytes()).hexdigest()
         for path in sorted(package.rglob('*')) if path.is_file()}
report = {'kind': 'static-payload-check', 'required_files': len(required), 'file_count': len(files),
          'windows_execution_tested': False, 'sha256': files}
out = root / 'artifacts/payload-check.json'
out.parent.mkdir(exist_ok=True)
out.write_text(json.dumps(report, indent=2) + '\n')
print(f'PASS: {len(required)} required files, x64 PE headers, both model digests; {len(files)} total files. Windows execution not tested.')
