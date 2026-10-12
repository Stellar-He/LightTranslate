# Third-party notices

## ECDICT

The English–Chinese word definitions in `lexicon.dat` are derived from
[skywind3000/ECDICT](https://github.com/skywind3000/ECDICT).
The dictionary is downloaded separately during packaging; its generated data
is included in the portable distribution, not in the source repository.

- Pinned source commit: `bc015ed2e24a7abef49fc6dbbb7fe32c1dadaf8b`
- Source file: `ecdict.csv`
- SHA-256: `1a6947e04785db63613a92e14903cdae7954f7e84860b10e68e5c7cbb3f9c3cf`
- License: MIT. The upstream copyright notice is preserved verbatim in
  [licenses/ECDICT-LICENSE.txt](licenses/ECDICT-LICENSE.txt).

The small programming vocabulary overrides in `Lexicon` are part of LightTranslate.

## STranslate

The built-in Microsoft translation request/signature implementation in
`Translator.Microsoft` and `Translator.Signature` was adapted from the
MIT-licensed [STranslate](https://github.com/STranslate/STranslate) Microsoft
translation plugin (the local implementation was prepared against version 2.0.10).
Its copyright and license are preserved in
[licenses/STranslate-LICENSE.txt](licenses/STranslate-LICENSE.txt).

The online providers are external services. The MIT license covers this project's
code and the credited components; it does not grant service access or imply any
affiliation with Microsoft or Tencent.
