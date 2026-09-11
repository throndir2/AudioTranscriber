# SQLite native runtime (Windows x64)

`e_sqlite3.dll` is the **unmodified official SQLite 3.53.4 Windows x64 DLL**,
renamed from `sqlite3.dll` to the name expected by SQLitePCLRaw. SQLite is
[in the public domain](https://sqlite.org/copyright.html).

The storage project targets x64, matching the desktop application. The native
DLL is copied to build and publish output, including through project references.
No download, native compilation, machine-wide installation, or provider
initialization override is needed at runtime.

## Provenance

- Download: <https://sqlite.org/2026/sqlite-dll-win-x64-3530400.zip>
- Release: <https://sqlite.org/releaselog/3_53_4.html>
- Source ID: `2026-07-24 19:02:57 bf7c7f30031888f4e796e429ab3978879485813aaca6f641c7b33e4e09459bcc`
- Archive SHA3-256 (verified against SQLite's download page):
  `deddee963c810d1eeac3ce5e15c7c41da21a1c54d7a39cf54fbf577d2f50de3a`
- DLL SHA-256:
  `ab57d0437795ecc757cb693f32ea224173fa9856594d95cfa6b5033e645cd1ec`
- DLL SHA3-256:
  `844d00bdf5ba9a52d61cd3fd244a7efffdb89d7da119701fb47c172043c5c1d3`

## Why this DLL is explicit

This official DLL was pinned while investigating native crashes on Windows.
A copied NuGet SQLite 3.53.4 DLL initially crashed on valid SQL as small as:

```sql
CREATE TABLE jobs(state TEXT, session_id TEXT);
SELECT state FROM jobs GROUP BY state;
```

Further dump analysis **did not establish a defective SQLite build**. In the
captured process, executable page RVA `0x122000` was entirely zero-filled.
The matching package file contained valid instructions at that location.
Executing the zero bytes at RVA `0x12203d` attempted a write to address `0x6`.
The same original NuGet DLL, with unchanged SHA-256
`6ad8e149f8ce3ed3716402b4b3a2268ebbdc7b64391b5fafed747e03bb1b9418`,
subsequently passed the native reproduction both from the package cache and
from a fresh copy, including 1,000 iterations.

A separate native testhost crash occurred before CLR or SQLite loaded. Its
dump showed zero-filled `.rdata` pages, including the import descriptors at
RVA `0x2373c`; the unresolved `LoadLibraryExW` import was then called as address
`0x23e8e`. Both failures therefore involve corrupted loaded image pages, not
evidence of invalid SQL, a SQLite ABI mismatch, or a managed test failure.
The underlying machine, file-copy, or image-mapping cause remains unverified.
Disabling pooling or changing SQL shape does not address that mechanism.

The official DLL above passed the native reproductions, individual FTS and
queue tests, and ten successive full storage-suite runs. It is retained as a
verified, reproducible runtime asset, **not as a proven fix for the broader
image-loading problem**. Its compiler differs from the NuGet build, but this
is not evidence that either compiler is defective.

`Microsoft.Data.Sqlite.Core` 10.0.12 and
`SQLitePCLRaw.config.e_sqlite3` 3.0.5 supply the managed provider without a
competing native DLL. If switching back to a standard native bundle, remove the
explicit DLL to avoid ambiguous resolution and validate clean build, test,
and publish output. The investigation does not justify blacklisting the NuGet
SQLite build.

## Updating

Check SQLite security advisories and release notes, obtain the official x64
archive over HTTPS, and verify its published SHA3-256 before replacing the DLL.
Update the provenance above and the minimum version in `NativeSqliteTests`.
Run the native regression test, the individual persistence tests, and repeated
full `AudioTranscriber.Storage.Tests` runs from a clean build. Check that
published output contains this DLL as well. NuGet vulnerability auditing covers
the managed dependencies, but does not audit this vendored native asset.
