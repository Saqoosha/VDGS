#!/usr/bin/env python3
"""Turn this project's CalVer release number into something Tauri will accept.

Releases are named for the day they were built - 2026.09.03, or 2026.09.01.3 for a fourth
build in one day. Tauri parses tauri.conf.json's `version` as SemVer, which forbids a
leading zero in a numeric identifier, so the file cannot carry the release number as
written and the bundle ends up reporting 0.1.0 forever.

    2026.09.03    -> 2026.9.3
    2026.09.01.3  -> 2026.9.1+3
    2026.09.01    -> 2026.9.1

The fourth component becomes SemVer build metadata rather than being dropped: without it
2026.09.01 and 2026.09.01.3 would both call themselves 2026.9.1, and two different
downloads claiming one version is worse than a version that reads oddly.

Anything that is not a plain number is passed through untouched, so a hand-set version
like 0.1.0 or 1.2.3-rc1 still works.
"""

import sys


def to_semver(version: str) -> str:
    parts = [p for p in version.split(".") if p != ""]
    if not parts:
        raise ValueError("empty version")
    # int() then str() is what removes the leading zero, and only for parts that are
    # entirely digits - a pre-release tag has to survive as it was written.
    nums = [str(int(p)) if p.isdigit() else p for p in parts]
    core = ".".join(nums[:3])
    extra = nums[3:]
    return core + ("+" + ".".join(extra) if extra else "")


if __name__ == "__main__":
    if len(sys.argv) != 2:
        sys.exit("usage: calver_to_semver.py <version>")
    print(to_semver(sys.argv[1]))
