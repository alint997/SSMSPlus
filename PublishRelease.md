# Publish a release (SSMS 21 / 22)

## Automatic (GitHub Actions)

The workflow `.github/workflows/build-release.yml` builds the extension on a Windows runner
(`build.cmd Release`) and, for tags starting with `v`, publishes a GitHub release with:

* `SSMSPlus-ssms22.zip` - `SSMSPlus\` folder + `install-ssms22.cmd`
* `SSMSPlus.vsix`

To publish a new version:

1. Update the version in `src/SSMSPlus/source.extension.vsixmanifest` (`Identity Version`)
   and `src/SSMSPlus/Properties/AssemblyInfo.cs` (`AssemblyFileVersion`).
   Do not change `AssemblyVersion` build number unless a new DB upgrade script is added
   (it is used as the local database version).
2. Commit and push to `master`.
3. Create and push the tag:

   ```
   git tag v7.5
   git push origin v7.5
   ```

4. The release appears on https://github.com/alint997/SSMSPlus/releases when the workflow finishes.

## Manual

1. Run `build.cmd` on a machine with Visual Studio 2022+ (VS extension development workload).
2. On GitHub: Releases > *Draft a new release*, create the tag (e.g. `v7.5`) and attach
   `dist\SSMSPlus-ssms22.zip` and `dist\SSMSPlus.vsix`.
