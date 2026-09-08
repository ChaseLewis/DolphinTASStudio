# Dolphin TAS Studio notices

Dolphin TAS Studio is an independent frontend. Application-owned source in this
repository is provided under GPL-2.0-or-later; the full GPL version 2 text is in
[LICENSE](LICENSE). Existing upstream copyright and license notices remain in force.

The native backend is a modified [Dolphin Libretro](https://github.com/libretro/dolphin)
checkout pinned to `e1e6d25fa1392b7d1bc05bf800c71b807a2bd2e0`.
Its modifications are retained in `native/patches/0001-tas-contract.patch` and
`native/patches/0002-tas-trace.patch`. Source retrieval and normal builds use
`scripts/build.ps1`; optional tracing uses `scripts/build-trace.ps1`.
Dolphin's `COPYING` and `LICENSES` describe its component licenses and are copied
into local published builds. Preserve those notices and corresponding source,
including these patches, when distributing the modified backend.

Managed and host dependencies retain their respective licenses:

- Avalonia and Dock: MIT.
- NAudio: MIT.
- Velopack update runtime and packaging tools: MIT; see `licenses/Velopack-LICENSE.txt`
  in source and `LICENSES/Velopack-LICENSE.txt` in distributions.
- .NET runtime and libraries: MIT and component-specific notices.
- Libretro API headers: MIT.
- Microsoft.Data.Sqlite: MIT.
- SQLitePCLRaw: Apache-2.0.
- SQLite: public domain.

NuGet supplies managed dependencies; their package/source notices remain authoritative.
The VS Code extension source uses the repository's GPL-2.0-or-later license.
Removed Lua/editor packages are no longer dependencies.

The local publish scripts produce a runnable development distribution. They are not
a complete external binary release process: review all bundled third-party notices
and corresponding-source delivery before distributing installers or binary archives.
Publishing this source repository does not by itself complete that release work.

Game images, BIOS images, personal projects, save data, and generated test artifacts
are not part of the repository. Native tests use locally supplied game images.
The Skies memory-map utilities contain reader code and addresses, not game assets.
