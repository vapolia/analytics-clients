# Vapolia Analytics: Clients SDK 

Doc: [Contract definition](clients/README.md)  
Legal: [What integrating a client commits you to](clients/OBLIGATIONS.md)  
Contributing: [Design notes](clients/README.code.md)  
Claude Code: the [`install-client` skill](plugins/analytics-client/skills/install-client/SKILL.md) walks the integration, from the package to the welcome popup. Add this repository as a plugin marketplace.

| Client | Runs on | Package |
|---|---|---|
| [Kotlin](clients/kotlin/README.md) | Android, minSdk 24 | `com.vapolia.analytics:analytics` |
| [Swift](clients/swift/README.md) | iOS 15+, macCatalyst | SwiftPM, product `VapoliaAnalytics` |
| [JS](clients/js/README.md) | Expo / React Native | `@vapolia/analytics` |
| [.NET](clients/dotnet/README.md) | MAUI, Android, iOS, Windows | `Vapolia.Analytics.Client` |
| [ASP.NET](clients/dotnet/README.md) | Blazor / ASP.NET Core | `Vapolia.Analytics.Client.AspNetCore` |
| [Go](clients/go/README.md) | Servers | `github.com/vapolia/analytics-clients/clients/go` |

[![NuGet][dotnet-nuget-img]][dotnet-nuget-link]
[![NuGet][aspnet-nuget-img]][aspnet-nuget-link]
[![npm][js-npm-img]][js-npm-link]

[dotnet-nuget-img]: https://img.shields.io/nuget/v/Vapolia.Analytics.Client
[dotnet-nuget-link]: https://www.nuget.org/packages/Vapolia.Analytics.Client/
[aspnet-nuget-img]: https://img.shields.io/nuget/v/Vapolia.Analytics.Client.AspNetCore
[aspnet-nuget-link]: https://www.nuget.org/packages/Vapolia.Analytics.Client.AspNetCore/
[js-npm-img]: https://img.shields.io/npm/v/@vapolia/analytics
[js-npm-link]: https://www.npmjs.com/package/@vapolia/analytics