#!/usr/bin/env python3
"""Validate a packed CLC Configuration package and write its SHA-256 sidecar."""

from __future__ import annotations

import argparse
import hashlib
import sys
import zipfile
from pathlib import Path
from xml.etree import ElementTree


PACKAGE_ID = "Clc.Configuration"
REPOSITORY_URL = "https://github.com/clcdpc/clc-configuration"


def local_name(tag: str) -> str:
    return tag.rsplit("}", 1)[-1]


def child_text(parent: ElementTree.Element, name: str) -> str:
    matches = [element for element in parent if local_name(element.tag) == name]
    if len(matches) != 1 or matches[0].text is None:
        raise ValueError(f"Expected exactly one non-empty '{name}' element.")
    return matches[0].text


def validate_package(package_path: Path, expected_version: str) -> str:
    if not package_path.is_file():
        raise ValueError(f"Package does not exist: {package_path}")

    expected_name = f"{PACKAGE_ID}.{expected_version}.nupkg"
    if package_path.name != expected_name:
        raise ValueError(f"Expected package filename '{expected_name}', found '{package_path.name}'.")

    with zipfile.ZipFile(package_path) as package:
        entries = {entry.filename.casefold(): entry for entry in package.infolist()}
        nuspecs = [entry for entry in package.infolist() if entry.filename.casefold().endswith(".nuspec")]
        if len(nuspecs) != 1:
            raise ValueError("Package must contain exactly one .nuspec file.")

        root = ElementTree.fromstring(package.read(nuspecs[0]))
        metadata_matches = [element for element in root if local_name(element.tag) == "metadata"]
        if len(metadata_matches) != 1:
            raise ValueError("Package nuspec must contain exactly one metadata element.")
        metadata = metadata_matches[0]

        actual_id = child_text(metadata, "id")
        actual_version = child_text(metadata, "version")
        authors = child_text(metadata, "authors")
        if actual_id != PACKAGE_ID:
            raise ValueError(f"Expected package ID '{PACKAGE_ID}', found '{actual_id}'.")
        if actual_version != expected_version:
            raise ValueError(f"Expected package version '{expected_version}', found '{actual_version}'.")
        if authors != "Central Library Consortium":
            raise ValueError(f"Unexpected package authors: '{authors}'.")

        license_element = next((element for element in metadata if local_name(element.tag) == "license"), None)
        if license_element is None or license_element.attrib.get("type") != "expression" or license_element.text != "MIT":
            raise ValueError("Package license must be the MIT SPDX expression.")

        repository = next((element for element in metadata if local_name(element.tag) == "repository"), None)
        if repository is None or repository.attrib.get("url") != REPOSITORY_URL or repository.attrib.get("type") != "git":
            raise ValueError("Package repository metadata is missing or incorrect.")

        if child_text(metadata, "readme") != "README.md":
            raise ValueError("Package readme metadata must point to README.md.")
        for required_entry in ("readme.md", "license", "buildtransitive/clc.configuration.targets"):
            if required_entry not in entries:
                raise ValueError(f"Package is missing required file '{required_entry}'.")

    digest = hashlib.sha256(package_path.read_bytes()).hexdigest()
    checksum_path = package_path.with_suffix(package_path.suffix + ".sha256")
    checksum_path.write_text(f"{digest}  {package_path.name}\n", encoding="utf-8")
    return digest


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--package", required=True, type=Path, help="Path to the .nupkg file to validate.")
    parser.add_argument("--version", required=True, help="Expected package version.")
    args = parser.parse_args(argv)

    try:
        digest = validate_package(args.package, args.version)
    except (OSError, ValueError, zipfile.BadZipFile, ElementTree.ParseError) as error:
        print(f"Package validation failed: {error}", file=sys.stderr)
        return 1

    print(f"Validated {PACKAGE_ID} {args.version}; SHA-256 {digest}.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
