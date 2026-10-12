# Windows acceptance fixtures

`acceptance.yaml` runs from this directory, synchronizing the fixture sources into workspace `acceptance`. It builds Win32/CMake, MFC/ATL, C++/CLI, WPF, WinForms, WinUI 3 and UWP examples, then exercises their visible UI from the Windows desktop worker.

```sh
./winix vm up dev --wait
./winix sync examples --config examples/acceptance.yaml
./winix run build --config examples/acceptance.yaml --wait
./winix run test --config examples/acceptance.yaml --wait
```

Or stop the development VM and use `./winix ci --config examples/acceptance.yaml` for a fresh overlay.

For a shorter native build/CTest check, use `./winix ci --config examples/native-ci.yaml`. Running it twice also checks that a file created by the first run is absent from the second VM.

These are test fixtures, not production app templates. Their package identity is `Winix.Smoke` / publisher `CN=Winix Development`; packaging uses the VM's development certificate. UI tests compare actual control values and save screenshots.
