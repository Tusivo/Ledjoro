# Making a release

Every Ledjoro release is signed. The signing key is yours alone: the private half stays on your computer (never on GitHub), and the public half is built into every `Ledjoro.exe`. **Check for Updates** installs a release only if its signature checks out, so a hijacked GitHub account cannot push a bad update to your users.

## One-time setup: make the signing key

```
cd C:\MyApps\Ledjoro
node scripts/release-sign.js keygen
```

It asks you to choose a passphrase (8 or more characters), then:

- saves the **private key**, locked with that passphrase, to `.ledjoro-signing\release-private.pem` in your user folder;
- writes the **public key** to `native\Ledjoro\release-key.txt`, which the build embeds in the program.

**Back up the private key file and remember the passphrase** (a password manager and a USB copy kept somewhere safe are both good). Never put the private key in the repository or on GitHub. If you lose it you cannot sign updates, and people on older versions would have to download the next version by hand. Commit `release-key.txt`; it is public by design.

The key is made once. `keygen` refuses to overwrite an existing key.

## Every release

1. Set the version in `package.json` and `native/Ledjoro/Ledjoro.csproj` (both `Version` and `InformationalVersion`, plus the two numeric versions), and update the README status line.
2. Run the tests and build: `npm test`, then `npm run build`. Close Ledjoro first, since Windows will not replace a running program.
3. Sign the program:
   ```
   node scripts/release-sign.js sign dist\Ledjoro.exe
   ```
   It asks for the passphrase, writes `dist\Ledjoro.exe.sig`, and checks the result against the public key before finishing. The version signed is the one in `package.json`; use `--version` to say otherwise.
4. On GitHub, **Draft a new release** with a tag such as `v0.10.0-beta.1` and attach **both** `Ledjoro.exe` and `Ledjoro.exe.sig` (and `Ledjoro.exe.sha256` if you like). Tick **Set as a pre-release** for betas.

A release without its `.sig` file is never offered by Check for Updates. A signature is tied to one version and one exact file: change the program after signing and it must be signed again.

## Checking a release yourself

```
node scripts/release-sign.js verify dist\Ledjoro.exe --version 0.10.0-beta.1
```

## Good to know

- The first build that contains the public key is the first one that can check updates. Builds made before the key existed show a message asking people to download the newest version from the releases page.
- A code-signing certificate (Authenticode) is a separate thing: it makes Windows show your name instead of the SmartScreen warning. This release signature does not replace it, and it does not depend on it.
