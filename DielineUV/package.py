from io import BytesIO
from pathlib import Path
from zipfile import ZIP_DEFLATED, ZipFile
import re


root = Path(__file__).resolve().parent
version = re.search(r'AssemblyVersion\("(\d+\.\d+\.\d+)\.0"\)',
                    (root / "DielineUV.cs").read_text(encoding="utf-8-sig"))[1]
dist = root / "dist"
dist.mkdir(exist_ok=True)
archive = dist / f"DielineUV-{version}-rhino7-rhino8.zip"

with ZipFile(archive, "w", ZIP_DEFLATED) as package:
    package.write(root / "README.md", "安装说明.txt")
    for rhino, target in (("Rhino7", "net48"), ("Rhino8", "net8.0")):
        assembly = (root / "bin" / "Release" / target / "DielineUV.rhp").read_bytes()
        if not assembly.startswith(b"MZ"):
            raise ValueError(f"Missing .NET assembly for {rhino}")
        package.writestr(f"{rhino}/DielineUV.rhp", assembly)
        mac_bytes = BytesIO()
        with ZipFile(mac_bytes, "w", ZIP_DEFLATED) as mac:
            mac.writestr("DielineUV.rhp/DielineUV.dll", assembly)
        package.writestr(f"Mac/DielineUV-{rhino}.macrhi", mac_bytes.getvalue())

with ZipFile(archive) as package:
    if package.testzip() is not None or len(package.namelist()) != 5:
        raise ValueError("Invalid installer archive")
    for rhino in ("Rhino7", "Rhino8"):
        with ZipFile(BytesIO(package.read(f"Mac/DielineUV-{rhino}.macrhi"))) as mac:
            if mac.read("DielineUV.rhp/DielineUV.dll") != package.read(f"{rhino}/DielineUV.rhp"):
                raise ValueError(f"Windows and Mac binaries differ for {rhino}")

print(archive.name)
