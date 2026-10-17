# Publish a release (SSMS 21 / 22 and SSMS 18 / 19 / 20)

## Automatic (GitHub Actions)

The workflow `.github/workflows/build-release.yml` builds the extension on a Windows runner
twice (`build.cmd Release`, then `build.cmd Release ssms20`) and, for tags starting with `v`, publishes a
GitHub release with:

* `SSMSPlus-ssms22.zip` - `SSMSPlus\` folder + `install-ssms22.cmd` (SSMS 21 / 22)
* `SSMSPlus.vsix` (SSMS 21 / 22)
* `SSMSPlus-ssms20.zip` - `SSMSPlus\` folder + `install-ssms20.cmd` (SSMS 18 / 19 / 20, experimental)

To publish a new version:

1. Update the version in `src/SSMSPlus/source.extension.vsixmanifest` (`Identity Version`)
   and `src/SSMSPlus/Properties/AssemblyInfo.cs` (`AssemblyFileVersion`).
   Do not change `AssemblyVersion` build number unless a new DB upgrade script is added
   (it is used as the local database version).
2. Commit and push to `master`.
3. Create and push the tag:

   ```
   git tag v7.7
   git push origin v7.7
   ```

4. The release appears on https://github.com/alint997/SSMSPlus/releases when the workflow finishes.

## Manual

1. Run `build.cmd`, then `build.cmd ssms20`, on a machine with Visual Studio 2022+ (VS extension development
   workload). The second build leaves `dist` alone and writes `dist-ssms20`.
2. On GitHub: Releases > *Draft a new release*, create the tag (e.g. `v7.7`) and attach
   `dist\SSMSPlus-ssms22.zip`, `dist\SSMSPlus.vsix` and `dist-ssms20\SSMSPlus-ssms20.zip`.
