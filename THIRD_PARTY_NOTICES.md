# Third-party notices

This file records the material third-party and inherited-resource obligations currently known for Aerochat. It supplements, and does not replace, the license text shipped with each work. A release owner must re-check the exact package versions, transitive dependencies, API terms, and asset provenance used by that release, then include the applicable license and notice text in source and binary distributions.

Aerochat source remains subject to the repository's root `LICENSE` file (MPL-2.0). No notice in this file grants rights to a trademark, hosted service, API content, or inherited art asset.

## Archived not-nullptr/Aerochat upstream

Aerochat is derived from the archived `not-nullptr/Aerochat` project, whose repository identifies the project as MPL-2.0.[1]

- **Upstream:** `not-nullptr/Aerochat`
- **License:** Mozilla Public License 2.0 (MPL-2.0)
- **Treatment in this repository:** Preserve the upstream copyright and license notices. Covered source files and modifications to covered files remain available under MPL-2.0; the root `LICENSE` must remain with the repository and any distribution.
- **Distribution note:** Do not imply that the MPL license clears separate Microsoft assets, Tenor content, or other third-party materials listed below. Those materials require their own review.

## XamlAnimatedGif 2.3.0

The WPF client references the NuGet package `XamlAnimatedGif` version `2.3.0` in `Aerochat/Aerochat.csproj`. NuGet package metadata and the 2.3.0 package specification identify its license as **Apache-2.0**, not BSD-2-Clause.[2][3]

- **Package:** `XamlAnimatedGif` 2.3.0
- **Purpose:** Animated GIF rendering in the WPF client
- **License:** Apache License 2.0 (SPDX: `Apache-2.0`)
- **Distribution note:** Preserve the package's copyright and license notices and include the Apache-2.0 license text or a clear notice location in any binary distribution that includes the package.

## SIPSorcery and SIPSorceryMedia.Windows (planned RTC dependency)

The current solution does not yet lock these packages as production dependencies. They are planned for WebRTC/RTC work in the connectivity layer:

- `SIPSorcery` — core SIP/WebRTC/RTP/ICE/SDP functionality
- `SIPSorceryMedia.Windows` — Windows audio/video media endpoints
- **Expected license family:** BSD 3-Clause (`BSD-3-Clause`) for the selected packages, subject to the exact release metadata and upstream license file.[4][5]

Before enabling or distributing an RTC build:

1. Record the exact versions in the project file and this notice.
2. Re-check the exact NuGet package metadata and upstream license files; package terms can change between releases. The current SIPSorcery package listing displays an additional upstream use-policy notice alongside its BSD 3-Clause label, so a generic BSD label must not be treated as final legal clearance.[4]
3. Include the selected packages' copyright, BSD conditions, disclaimers, and any version-specific additional terms in the distribution notice bundle.
4. Keep the dependencies behind `Aerochat.Connectivity`; do not place media/networking types in Presentation or Controls.

## Microsoft Windows Live Messenger 2009 inherited assets

The repository retains Windows Live Messenger 2009-derived visual art, emoji, sounds, and related resources to preserve the product's private/development WLM visual shell. These materials are separate from Aerochat's MPL-2.0 source license and remain subject to Microsoft's and other rightsholders' rights.

- Treat these assets as **private/development use only** in the current state.
- Do not represent them as Aerochat-original or MPL-licensed.
- **Standing release debt:** replace the inherited art, emoji, and sound assets with original, permission-cleared, or appropriately licensed alternatives before any public binary, installer, asset bundle, or hosted distribution.
- A public release must not rely on this notice as permission to distribute the inherited assets.

## Tenor API v2

Tenor is a proprietary hosted API/service, not an open-source dependency. If the GIF search proxy is enabled, the operator must keep the Tenor API key server-side, follow the current Google/Tenor API terms and developer policies, and re-check service availability before deployment.[6]

- GIF search queries and request metadata are sent to Tenor when a user invokes the feature; disclose that transfer in the deployment's privacy notice.
- Use the required content-safety parameters, including the appropriate `contentfilter` setting, for the operator's audience and policy.[7]
- Tenor requires attribution for content retrieved from its API. The integration must render an attribution such as **Powered by Tenor**, **Search Tenor**, or **Via Tenor** in the location required by the use case.[7]
- In particular, attribution fields must be rendered where GIF results are shown; they must not be hidden, removed, or replaced by unrelated source labels.
- Do not modify or reorder Tenor search results, disable Tenor-provided links/branding, or treat Tenor GIF content as covered by the Aerochat source license.[6]

## Release notice checklist

Before publishing source, binaries, installers, or a hosted service:

- Ship the root MPL-2.0 `LICENSE` and preserve upstream notices.
- Include the exact Apache-2.0 notice for `XamlAnimatedGif` 2.3.0.
- Audit all direct and transitive NuGet packages and add their notices.
- If RTC is enabled, record and review the exact SIPSorcery package versions and license files.
- Remove or replace the inherited Microsoft/WLM assets, or obtain written rights, before public binary distribution.
- Keep Tenor API credentials out of the repository and verify attribution/content-filter behavior in the release build.
- Re-check this file whenever a dependency, asset, API, or distribution format changes.

## Sources

[1] [not-nullptr/Aerochat archive](https://github.com/not-nullptr/Aerochat)

[2] [NuGet: XamlAnimatedGif 2.3.0](https://www.nuget.org/packages/XamlAnimatedGif/2.3.0)

[3] [XamlAnimatedGif 2.3.0 NuGet package specification](https://api.nuget.org/v3-flatcontainer/xamlanimatedgif/2.3.0/xamlanimatedgif.nuspec)

[4] [NuGet: SIPSorcery](https://www.nuget.org/packages/SIPSorcery)

[5] [NuGet: SIPSorceryMedia.Windows](https://www.nuget.org/packages/SIPSorceryMedia.Windows)

[6] [Tenor API Terms of Service](https://developers.google.com/tenor/guides/api-terms)

[7] [Tenor API documentation: content filtering and attribution](https://tenor.com/gifapi/documentation)
