"""Adds or updates UI strings in localization/catalog.json and the locale packs.

Input: a JSON file mapping each key to its English text, a context line and the translations:
    {"TopBar.Home": {"text": "Home", "context": "Top bar navigation item", "de": "Start", "ar": "..."}}

For every key the script writes the catalog entry (text, context, and `sources` = the files that
use the key, found by scanning src/LightWeaver, src/LightWeaver.Core and src/LightWeaver.WinUI for L.Text/L.Format
calls and {loc:Text ...} XAML references), and each translation with its source hash
(sha256 of the English text), so the translation editor and the runtime treat it as current.

Usage: python -I tools/Localization/merge-strings.py <strings.json>
       python -I tools/Localization/merge-strings.py --refresh-sources
The second form only re-records `sources` for every catalog key still used, after code moved.
"""
import hashlib
import json
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent.parent
SCAN = [ROOT / "src" / "LightWeaver", ROOT / "src" / "LightWeaver.Core", ROOT / "src" / "LightWeaver.WinUI"]
USE = re.compile(r'L\.(?:Text|Format|Resolve)\(\s*"([^"]+)"|\{loc:Text\s+(?:Key=)?([^}\s]+)\s*\}')


def sources() -> dict[str, set[str]]:
    found: dict[str, set[str]] = {}
    for base in SCAN:
        for path in base.rglob("*"):
            if path.suffix not in (".cs", ".xaml") or {"bin", "obj"} & set(path.parts):
                continue
            rel = path.relative_to(ROOT).as_posix()
            for match in USE.finditer(path.read_text(encoding="utf-8-sig")):
                found.setdefault(match.group(1) or match.group(2), set()).add(rel)
    return found


def refresh_sources() -> None:
    used = sources()
    catalog_path = ROOT / "localization" / "catalog.json"
    catalog = json.loads(catalog_path.read_text(encoding="utf-8-sig"))
    changed = 0
    for key, entry in catalog["strings"].items():
        if key in used and entry.get("sources") != sorted(used[key]):
            entry["sources"] = sorted(used[key])
            changed += 1
    catalog_path.write_text(json.dumps(catalog, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"{changed} source lists refreshed")


def main() -> None:
    if sys.argv[1] == "--refresh-sources":
        refresh_sources()
        return
    spec = json.loads(pathlib.Path(sys.argv[1]).read_text(encoding="utf-8"))
    used = sources()
    catalog_path = ROOT / "localization" / "catalog.json"
    catalog = json.loads(catalog_path.read_text(encoding="utf-8-sig"))
    missing = [k for k in spec if k not in used]
    if missing:
        sys.exit("keys not used in source: " + ", ".join(missing))
    # Validate every locale before writing anything, so a gap never leaves a partial merge.
    locales = sorted((ROOT / "localization" / "locales").glob("*.json"))
    packs = {loc: json.loads(loc.read_text(encoding="utf-8-sig")) for loc in locales}
    gaps = [f"{key}: no {pack['language']}" for pack in packs.values() for key, entry in spec.items() if pack["language"] not in entry]
    if gaps:
        sys.exit("missing translations: " + ", ".join(gaps))
    for key, entry in spec.items():
        catalog["strings"][key] = {"text": entry["text"], "context": entry["context"], "sources": sorted(used[key])}
    catalog["strings"] = dict(sorted(catalog["strings"].items(), key=lambda kv: kv[0]))
    catalog_path.write_text(json.dumps(catalog, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

    for locale, pack in packs.items():
        lang = pack["language"]
        for key, entry in spec.items():
            pack["strings"][key] = entry[lang]
            pack.setdefault("sourceHashes", {})[key] = hashlib.sha256(entry["text"].encode("utf-8")).hexdigest()
        pack["strings"] = dict(sorted(pack["strings"].items()))
        pack["sourceHashes"] = dict(sorted(pack["sourceHashes"].items()))
        locale.write_text(json.dumps(pack, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"{len(spec)} strings merged")


if __name__ == "__main__":
    main()
