#!/usr/bin/env python3
"""Generates THIRD-PARTY-NOTICES.txt. The output is never edited by hand. Needs Python 3.8+.

Inputs:
  * the restored package graph: src/*/obj/project.assets.json (run `dotnet restore` first);
  * the licence and notice files shipped inside each restored package;
  * third-party/<component>/METADATA plus any committed texts there, for texts a package
    does not ship (fetched from the upstream repository; METADATA records the source and date);
  * third-party/models/<model>/METADATA for every ModelAsset URL in ModelCatalog.cs.

Usage:
  python3 scripts/generate-notices.py           regenerate the file
  python3 scripts/generate-notices.py --check   fail if it is out of date (CI, release and pack gate)

The script also fails when the graph and the inputs disagree: a shipped package that no
component covers, a component entry that matches no package, a package whose declared licence
differs from its METADATA, a declared licence file that is missing, or a catalogue model with no
entry. That is how graph drift is caught.

Coupling: src/Directory.Build.props runs `--check` before packing (CI pack job and local packs),
and release.yml runs it before tagging. scripts/tests covers every failure path. Changing the command line here means changing both callers too.
"""
from __future__ import annotations

import fnmatch
import hashlib
import json
import os
import re
import sys
import xml.etree.ElementTree as ElementTree
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "THIRD-PARTY-NOTICES.txt"
THIRD_PARTY = ROOT / "third-party"
MODELS = THIRD_PARTY / "models"
MODEL_CATALOG = ROOT / "src" / "Foliant.Models" / "ModelCatalog.cs"

RULE = "=" * 80
TEXT_NAME = re.compile(
    r"^(licen[cs]e|copying|notice|third-?party-?notices|thirdpartynotices)(\.(txt|md))?$", re.I)
COMMITTED_NAME = re.compile(r"^(METADATA|(LICENSE|NOTICE)-[\w.-]+)$")
MODEL_ASSET = re.compile(r"ModelAsset\s+\w+\s*\{\s*get;\s*\}\s*=\s*new\((.*?)\);", re.S)
URL = re.compile(r'"(https?://[^"]+)"')
# C0 and C1 control characters (C1 includes the 8-bit CSI, U+009B).
CONTROL = re.compile(r"[\x00-\x1f\x7f-\x9f]")
TEXT_CONTROL = re.compile(r"[\x00-\x08\x0b-\x1f\x7f-\x9f]")

# Licence permission policy (~/.claude/CLAUDE.md §4). A package whose declared licence is not in
# this list, or a component whose texts mention a copyleft licence, needs the owner's recorded
# decision in its METADATA ("Approved:" or "Copyleft-review:"), or the gate fails.
PERMITTED = {"MIT", "Apache-2.0", "BSD-2-Clause", "BSD-3-Clause", "ISC", "Zlib",
             "0BSD", "MIT-0", "Unlicense", "CC0-1.0"}
# Copyleft, share-alike and non-commercial/no-derivatives terms (the §4 STOP list), any case.
# Words may wrap across lines in licence headers, so they are joined by any whitespace.
_W = r"\s+"
COPYLEFT = re.compile(
    rf"\b(GNU({_W}(Lesser|Library|Affero))?{_W}General{_W}Public{_W}License|[AL]?GPL(v?\d(\.\d)?)?|"
    rf"Mozilla{_W}Public{_W}License|MPL[ -]?\d|Eclipse{_W}Public{_W}License|EPL-\d|EUPL|"
    rf"European{_W}Union{_W}Public{_W}Licen[cs]e|MS-RL|Microsoft{_W}Reciprocal{_W}License|OSL-\d|"
    rf"Open{_W}Software{_W}License|CDDL|Common{_W}Development{_W}and{_W}Distribution|"
    rf"Server{_W}Side{_W}Public{_W}License|SSPL|Common{_W}Public{_W}License|CeCILL|"
    r"NonCommercial|non[-\s]?commercial\s+(use|purposes?)|Share[- ]?Alike|No[- ]?Deriv(atives|s)|CC[- ]BY[- ](SA|NC|ND))\b", re.I)
DECISIONS = ("Approved", "Copyleft-review")


class InputError(Exception):
    pass


def clean(value: str) -> str:
    """Package-derived text, made safe for a terminal or CI log (no control characters, so no
    ANSI escapes and no line that a CI runner could read as a workflow command)."""
    return CONTROL.sub(lambda m: f"\\x{ord(m.group()):02x}", value)


def read_text(path: Path) -> str:
    data = path.read_bytes()
    try:
        if data.startswith((b"\xff\xfe", b"\xfe\xff")):
            text = data.decode("utf-16")
        else:
            text = data.decode("utf-8-sig")
    except UnicodeDecodeError:
        # A few upstream licence files are Windows-1252 or Latin-1 (e.g. a bare 0xA9 copyright
        # sign). Latin-1 maps every byte, so the fallback always succeeds the same way everywhere.
        try:
            text = data.decode("cp1252")
        except UnicodeDecodeError:
            text = data.decode("latin-1")
    if "\x00" in text:
        raise InputError(f"{path}: not a text file (contains NUL characters)")
    # Form feeds are page breaks in old licence texts; drop them rather than escape them.
    text = text.replace("\r\n", "\n").replace("\r", "\n").replace("\f", "").rstrip("\n") + "\n"
    # The output is read in terminals and logs: escape control characters other than tab and
    # newline (e.g. ANSI escape sequences, form feeds) rather than copy them.
    return TEXT_CONTROL.sub(lambda m: f"\\x{ord(m.group()):02x}", text)


def read_metadata(path: Path) -> dict[str, str]:
    if not path.is_file():
        raise InputError(f"{path.relative_to(ROOT)} is missing")
    fields: dict[str, str] = {}
    for line in read_text(path).splitlines():
        if not line.strip():
            continue
        key, sep, value = line.partition(":")
        if not sep:
            raise InputError(f"{path.relative_to(ROOT)}: line without 'Key: value': {line!r}")
        if key.strip() in fields:
            raise InputError(f"{path.relative_to(ROOT)}: '{key.strip()}' appears more than once")
        fields[key.strip()] = value.strip()
    for key in ("Component", "Declared", "Source"):
        if key not in fields:
            raise InputError(f"{path.relative_to(ROOT)}: missing '{key}'")
    return fields


def split_list(value: str) -> list[str]:
    return [item.strip() for item in value.split(",") if item.strip()]


def is_packable(project: Path) -> bool:
    """IsPackable defaults to true in src/Directory.Build.props; a project opts out with an
    unconditional <IsPackable>false</IsPackable>. Coupled to MSBuild's evaluated $(IsPackable),
    which the pack-time gate uses: a conditional IsPackable would make the two disagree, so it
    is rejected (see the note in src/Directory.Build.props)."""
    try:
        root = ElementTree.parse(project).getroot()
    except ElementTree.ParseError as error:
        raise InputError(f"{project.relative_to(ROOT)}: {error}") from None
    values = [e for e in root.iter() if e.tag.rsplit("}", 1)[-1] == "IsPackable"]
    if any(e.get("Condition") is not None for e in values):
        raise InputError(f"{project.relative_to(ROOT)}: conditional <IsPackable> is not supported "
                         "by the notices gate; set it unconditionally")
    return not any((e.text or "").strip().lower() == "false" for e in values)


def load_graph() -> tuple[dict[str, set[str]], list[Path]]:
    """Packages that ship with the packable src projects, with their versions.

    Walks each target's dependency tree from the project's direct references, leaving out
    references marked PrivateAssets="all" (build-time tools such as MinVer) and anything
    reachable only through them.
    """
    graph: dict[str, set[str]] = {}
    folders: list[Path] = []
    projects = [p for p in sorted((ROOT / "src").glob("*/*.csproj")) if is_packable(p)]
    if not projects:
        raise InputError("no packable projects found under src/")
    for project in projects:
        assets = project.parent / "obj" / "project.assets.json"
        if not assets.is_file():
            raise InputError(f"{assets.relative_to(ROOT)} not found; run 'dotnet restore Foliant.sln' first")
        data = json.loads(assets.read_text(encoding="utf-8"))
        for folder in data["packageFolders"]:
            if Path(folder) not in folders:
                folders.append(Path(folder))
        frameworks = data["project"]["frameworks"]
        for target_name, libraries in data["targets"].items():
            name = target_name.split("/")[0]
            # A platform target (net10.0-windows7.0) is keyed by its alias (net10.0-windows).
            framework = frameworks.get(name) or next(
                (f for f in frameworks.values() if f.get("framework") == name), None)
            if framework is None:
                raise InputError(f"{assets.relative_to(ROOT)}: no framework entry for target {name}")
            by_id = {key.partition("/")[0].lower(): (key, lib) for key, lib in libraries.items()}
            pending = [dep.lower() for dep, spec in framework.get("dependencies", {}).items()
                       if spec.get("suppressParent", "").lower() != "all"]
            seen: set[str] = set()
            while pending:
                dep = pending.pop()
                if dep in seen or dep not in by_id:
                    continue
                seen.add(dep)
                key, library = by_id[dep]
                if library.get("type") == "package":
                    package_id, _, version = key.partition("/")
                    graph.setdefault(package_id, set()).add(version)
                pending.extend(d.lower() for d in library.get("dependencies", {}))
    return graph, folders


def package_dir(folders: list[Path], package_id: str, version: str) -> Path:
    for folder in folders:
        candidate = folder / package_id.lower() / version.lower()
        if candidate.is_dir():
            return candidate
    raise InputError(f"{package_id} {version} is not in any restored package folder; "
                     "run 'dotnet restore Foliant.sln' first")


def declared_licence(root: Path, package_id: str) -> tuple[str, Path | None]:
    """The nuspec's licence: an SPDX expression, 'file' (with the file's path), or 'none'."""
    nuspec = root / f"{package_id.lower()}.nuspec"
    if not nuspec.is_file():
        raise InputError(f"{nuspec} not found; run 'dotnet restore Foliant.sln' first")
    # Python's expat parser does not fetch external entities, and limits entity expansion.
    try:
        tree = ElementTree.parse(nuspec)
    except ElementTree.ParseError as error:
        raise InputError(f"{package_id}: unreadable nuspec ({clean(str(error))}); "
                         "run 'dotnet restore Foliant.sln' first") from None
    element = next((e for e in tree.getroot().iter() if e.tag.rsplit("}", 1)[-1] == "license"), None)
    if element is None or not (element.text or "").strip():
        return "none", None
    value = element.text.strip()
    if element.get("type") != "file":
        return value, None
    path = (root / value.replace("\\", "/")).resolve()
    if root.resolve() not in path.parents or not path.is_file():
        raise InputError(f"{package_id} declares licence file '{clean(value)}', which is not in the package")
    return "file", path


def shipped_texts(root: Path, licence_file: Path | None) -> list[Path]:
    # Resolved, so a package folder reached through a symbolic link cannot list a file twice,
    # and contained, so a link inside a package cannot pull an outside file into the notices.
    # Links are resolved and checked before anything follows or stats them.
    base = root.resolve()
    texts = set()
    for entry in root.iterdir():
        if not TEXT_NAME.match(entry.name):
            continue
        target = entry.resolve()
        if base not in target.parents:
            raise InputError(f"{clean(root.name)}: {clean(entry.name)} points outside the package")
        if target.is_file():
            texts.add(target)
    if licence_file is not None:
        texts.add(licence_file)
    return sorted(texts, key=lambda p: p.name)


def is_permitted(expression: str) -> bool:
    """True when an SPDX expression needs no owner decision under PERMITTED.

    A flat 'A OR B' needs one permitted choice and 'A AND B' needs all. A grouped expression
    is judged conservatively: every licence in it must be permitted."""
    if "(" in expression or ")" in expression:
        atoms = [a for a in re.split(r"[()\s]+", expression) if a and a not in ("OR", "AND", "WITH")]
        return bool(atoms) and all(a in PERMITTED for a in atoms)
    if " OR " in expression:
        return any(is_permitted(part) for part in expression.split(" OR "))
    return all(part.strip() in PERMITTED for part in expression.split(" AND "))


def policy_errors(where: str, meta: dict[str, str], texts: list[str]) -> list[str]:
    """Licence permission gate: unlisted licences and copyleft terms need a recorded decision."""
    errors = [f"{where}: '{key}:' is empty; record who decided, when and why"
              for key in DECISIONS if key in meta and not meta[key]]
    if not is_permitted(meta["Declared"]) and "Approved" not in meta:
        errors.append(f"{where}: licence '{meta['Declared']}' is not in the permitted list; record the "
                      "owner's decision as 'Approved: <who> <date>: <reason>' in METADATA")
    hits = sorted({m.group(0) for text in texts for m in COPYLEFT.finditer(text)})
    if hits and "Copyleft-review" not in meta:
        errors.append(f"{where}: texts mention copyleft terms ({', '.join(hits[:8])}); record the owner's "
                      "review as 'Copyleft-review: <who> <date>: <finding>' in METADATA")
    return errors


def text_order(name: str) -> tuple[int, str]:
    # Licences first, then notices, each alphabetically, so output order is stable.
    return (0 if name.lower().startswith(("licen", "copying")) else 1, name.lower())


def committed_texts(component: Path) -> list[Path]:
    texts = []
    for path in component.iterdir():
        if path.name.startswith("."):
            continue  # Finder and editor files; never inputs
        if path.is_symlink():
            raise InputError(f"{path.relative_to(ROOT)}: inputs must be regular files, not links")
        if not path.is_file() or not (COMMITTED_NAME.match(path.name) or TEXT_NAME.match(path.name)):
            raise InputError(f"{path.relative_to(ROOT)}: unexpected input; texts are LICENSE*, "
                             "THIRD-PARTY-NOTICES* or NOTICE-*")
        if path.name != "METADATA":
            texts.append(path)
    return sorted(texts, key=lambda p: text_order(p.name))


def match_components(graph: dict[str, set[str]], components: dict[str, dict[str, str]]) -> dict[str, str]:
    """Maps each package to exactly one component (exact ids win over patterns)."""
    exact: dict[str, str] = {}
    patterns: list[tuple[str, str]] = []
    for name, meta in components.items():
        for entry in split_list(meta.get("Packages", "")):
            if any(c in entry for c in "*?["):
                patterns.append((entry, name))
            elif entry in exact:
                raise InputError(f"package {entry} is listed by both {exact[entry]} and {name}")
            else:
                exact[entry] = name

    owner: dict[str, str] = {}
    errors: list[str] = []
    for package_id in sorted(graph, key=str.lower):
        if package_id in exact:
            owner[package_id] = exact[package_id]
            continue
        hits = sorted({name for pattern, name in patterns if fnmatch.fnmatchcase(package_id, pattern)})
        if len(hits) == 1:
            owner[package_id] = hits[0]
        elif not hits:
            errors.append(f"package {package_id} is not covered by any third-party/*/METADATA")
        else:
            errors.append(f"package {package_id} matches several components: {', '.join(hits)}")

    for entry, name in exact.items():
        if entry not in graph:
            errors.append(f"third-party/{name}: package {entry} is not in the shipped graph")
    for pattern, name in patterns:
        if not any(owner.get(p) == name and fnmatch.fnmatchcase(p, pattern) for p in graph):
            errors.append(f"third-party/{name}: pattern {pattern} matches no package")
    if errors:
        raise InputError("\n".join(errors))
    return owner


def generate() -> str:
    graph, folders = load_graph()
    components = {
        d.name: read_metadata(d / "METADATA")
        for d in sorted(THIRD_PARTY.iterdir(), key=lambda p: p.name)
        if d.is_dir() and d != MODELS
    }
    owner = match_components(graph, components)

    out: list[str] = [
        "Foliant third-party notices",
        "",
        "Foliant is licensed under the Apache License 2.0 (see LICENSE). Its packages depend on,",
        "and its native assets include, the third-party components below, each under its own",
        "licence. This file is generated by scripts/generate-notices.py from the restored package",
        "graph and the inputs in third-party/; do not edit it by hand.",
        "",
        RULE,
        "PART 1. PACKAGES",
        RULE,
    ]

    errors: list[str] = []
    for name, meta in components.items():
        packages = sorted((p for p in owner if owner[p] == name), key=str.lower)
        out += ["", RULE, f"Component: {meta['Component']}"]
        out.append("Packages: " + ", ".join(f"{p} {'/'.join(sorted(graph[p]))}" for p in packages))
        # Approved/Copyleft-review are internal records for the gate; they are not published.
        # Source-offer is published: MPL and similar licences require telling recipients of the
        # binaries where the covered source can be obtained.
        for key in ("Declared", "Source", "Version", "Source-offer", "Texts", "Retrieved"):
            if key in meta:
                out.append(f"{key}: {meta[key]}")
        out.append(RULE)

        # Committed texts (fetched upstream) first, then texts shipped inside the packages,
        # each distinct text once, with the packages that ship it.
        committed = committed_texts(THIRD_PARTY / name)
        # Texts fetched from upstream, and owner decisions, are tied to one release: a version bump
        # must refresh them (and the Version field), or the gate fails.
        if committed or "Version" in meta or any(key in meta for key in DECISIONS):
            pinned = meta.get("Version")
            actual = sorted({v for p in packages for v in graph[p]})
            if pinned is None:
                errors.append(f"third-party/{name}/METADATA: committed texts and decisions need 'Version:' "
                              f"(the release they were fetched for); graph has {', '.join(actual)}")
            elif actual != [pinned]:
                errors.append(f"third-party/{name}: texts were fetched for {pinned}, but the graph has "
                              f"{', '.join(actual)}; refresh the texts and 'Version:'")
        for path in committed:
            out += ["", f"--- {path.name} ---", "", read_text(path).rstrip("\n")]

        shipped: dict[str, tuple[str, str, list[str]]] = {}
        declared_files: set[str] = set()  # a package's declared licence file is licence text, whatever its name
        for package_id in packages:
            for version in sorted(graph[package_id]):
                root = package_dir(folders, package_id, version)
                licence, licence_file = declared_licence(root, package_id)
                if licence != meta["Declared"]:
                    errors.append(f"{package_id} {version} declares licence '{clean(licence)}', "
                                  f"third-party/{name}/METADATA says '{meta['Declared']}'")
                if licence_file is not None:
                    declared_files.add(licence_file.name)
                for path in shipped_texts(root, licence_file):
                    text = read_text(path)
                    digest = hashlib.sha256(text.encode("utf-8")).hexdigest()
                    entry = shipped.setdefault(digest, (path.name, text, []))
                    entry[2].append(f"{package_id} {version}")
        for file_name, text, sources in sorted(shipped.values(), key=lambda e: (text_order(e[0]), e[2][0])):
            out += ["", f"--- {file_name} (shipped in {', '.join(sources)}) ---", "", text.rstrip("\n")]
        errors += policy_errors(f"third-party/{name}", meta,
                                [read_text(p) for p in committed] + [e[1] for e in shipped.values()])
        names = [p.name for p in committed] + [e[0] for e in shipped.values()]
        if not declared_files and not any(text_order(n)[0] == 0 for n in names):
            errors.append(f"third-party/{name}: no licence text (LICENSE*/COPYING*) committed or "
                          "shipped in its packages")
    if errors:
        raise InputError("\n".join(errors))

    out += generate_models()
    return "\n".join(out) + "\n"


def catalogue_urls() -> set[str]:
    source = read_text(MODEL_CATALOG)
    if re.search(r'\$@?"[^"]*https?://', source):
        raise InputError("ModelCatalog.cs: model URLs must be plain string literals (no interpolation), "
                         "so this gate can see every model")
    urls: set[str] = set()
    for match in MODEL_ASSET.finditer(source):
        found = URL.findall(match.group(1))
        if len(found) != 1:
            raise InputError("ModelCatalog.cs: every ModelAsset needs exactly one literal https URL; "
                             f"found {len(found)} in: {' '.join(match.group(1).split())[:120]}")
        urls.add(found[0])
    if not urls:
        raise InputError("ModelCatalog.cs: no ModelAsset entries found")
    # Every URL in the file must belong to a recognised ModelAsset declaration, so a model
    # declared another way cannot slip past the licence check.
    stray = sorted(set(URL.findall(source)) - urls)
    if stray:
        raise InputError("ModelCatalog.cs: URL outside a 'ModelAsset X { get; } = new(...)' entry: "
                         + ", ".join(stray))
    return urls


def generate_models() -> list[str]:
    catalogue = catalogue_urls()
    out = [
        "",
        RULE,
        "PART 2. MODELS DOWNLOADED AT RUNTIME",
        "These files are not included in Foliant's packages. Foliant downloads them from their",
        "publishers on first use (see ModelCatalog); they are listed here for completeness.",
        RULE,
    ]
    covered: dict[str, str] = {}
    errors: list[str] = []
    if not MODELS.is_dir():
        raise InputError("third-party/models is missing")
    for model in sorted((d for d in MODELS.iterdir() if d.is_dir()), key=lambda p: p.name):
        meta = read_metadata(model / "METADATA")
        assets = split_list(meta.get("Assets", ""))
        for url in assets:
            if url in covered:
                errors.append(f"{url} is listed by both models/{covered[url]} and models/{model.name}")
            covered[url] = model.name
            if url not in catalogue:
                errors.append(f"third-party/models/{model.name}: {url} is not in ModelCatalog.cs")
        errors += policy_errors(f"third-party/models/{model.name}", meta, [])
        out += ["", f"Component: {meta['Component']}"]
        out += [f"Asset: {url}" for url in assets]
        for key in ("Declared", "Source", "Texts", "Retrieved"):
            if key in meta:
                out.append(f"{key}: {meta[key]}")
    errors += [f"ModelCatalog.cs URL {u} has no third-party/models entry"
               for u in sorted(catalogue - covered.keys())]
    if errors:
        raise InputError("\n".join(errors))
    return out


def main(argv: list[str]) -> int:
    check = argv[1:] == ["--check"]
    if argv[1:] and not check:
        print(__doc__, file=sys.stderr)
        return 2
    try:
        text = generate()
    except InputError as error:
        print(f"error: {error}", file=sys.stderr)
        return 1
    except (OSError, KeyError, ValueError) as error:  # ValueError covers malformed JSON
        print(f"error: cannot read the inputs: {error!r}; run 'dotnet restore Foliant.sln' first",
              file=sys.stderr)
        return 1
    data = text.encode("utf-8")
    if check:
        # .gitattributes keeps this file LF; normalise anyway so a CRLF checkout still compares.
        if not OUT.is_file() or OUT.read_bytes().replace(b"\r\n", b"\n") != data:
            print("error: THIRD-PARTY-NOTICES.txt is out of date; run "
                  "'python3 scripts/generate-notices.py' and commit the result", file=sys.stderr)
            return 1
        return 0
    # Write to a temporary name and rename, so a failure never leaves a truncated file.
    temp = OUT.with_name(OUT.name + ".tmp")
    temp.write_bytes(data)
    os.replace(temp, OUT)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
