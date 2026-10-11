# Üçüncü taraf bileşenler ve lisansları

Bu dosya `scripts/third_party_notices.py` ile üretilir; elle düzenlemeyin. Yalnız **dağıtılan** bileşenleri kapsar:
derlenmiş arayüz paketine giren npm paketleri ve API image'ına giren NuGet paketleri. Geliştirme/test araçları (Vite, TypeScript, ESLint,
Vitest, Playwright, xUnit, Testcontainers vb.) dağıtılmaz ve burada listelenmez. Taban image'lar (.NET, Node, PostgreSQL) kendi lisanslarıyla gelir.
Web paketlerinin tam lisans metinleri arayüzle birlikte `/third-party-notices.txt` adresinden sunulur.

## Web (npm) — 26 paket

| Paket | Sürüm | Lisans | Telif satırı (paketin lisans dosyasından) |
|---|---|---|---|
| @microsoft/signalr | 10.0.11 | MIT | belirtilmemiş |
| @tanstack/query-core | 5.104.1 | MIT | Copyright (c) 2021-present Tanner Linsley |
| @tanstack/react-query | 5.104.1 | MIT | Copyright (c) 2021-present Tanner Linsley |
| abort-controller | 3.0.0 | MIT | Copyright (c) 2017 Toru Nagashima |
| cookie | 1.1.1 | MIT | Copyright (c) 2012-2014 Roman Shtylman <shtylman@gmail.com> |
| event-target-shim | 5.0.1 | MIT | Copyright (c) 2015 Toru Nagashima |
| eventsource | 2.0.2 | MIT | Copyright (c) EventSource GitHub organisation |
| fetch-cookie | 2.2.0 | Unlicense | belirtilmemiş |
| node-fetch | 2.7.0 | MIT | Copyright (c) 2016 David Frank |
| psl | 1.15.0 | MIT | Copyright (c) 2017 Lupo Montero lupomontero@gmail.com |
| punycode | 2.3.1 | MIT | belirtilmemiş |
| querystringify | 2.2.0 | MIT | Copyright (c) 2015 Unshift.io, Arnout Kazemier,  the Contributors. |
| react | 19.3.0 | MIT | Copyright (c) Meta Platforms, Inc. and affiliates. |
| react-dom | 19.3.0 | MIT | Copyright (c) Meta Platforms, Inc. and affiliates. |
| react-router | 7.18.4 | MIT | Copyright (c) React Training LLC 2015-2019 |
| react-router-dom | 7.18.4 | MIT | Copyright (c) React Training LLC 2015-2019 |
| requires-port | 1.0.0 | MIT | Copyright (c) 2015 Unshift.io, Arnout Kazemier,  the Contributors. |
| scheduler | 0.28.0 | MIT | Copyright (c) Meta Platforms, Inc. and affiliates. |
| set-cookie-parser | 2.7.2 | MIT | Copyright (c) 2015 Nathan Friedly <nathan@nfriedly.com> (http://nfriedly.com/) |
| tough-cookie | 4.1.4 | BSD-3-Clause | Copyright (c) 2015, Salesforce.com, Inc. |
| tr46 | 0.0.3 | MIT | belirtilmemiş |
| universalify | 0.2.0 | MIT | Copyright (c) 2017, Ryan Zimmerman <opensrc@ryanzim.com> |
| url-parse | 1.5.10 | MIT | Copyright (c) 2015 Unshift.io, Arnout Kazemier,  the Contributors. |
| webidl-conversions | 3.0.1 | BSD-2-Clause | Copyright (c) 2014, Domenic Denicola |
| whatwg-url | 5.0.0 | MIT | Copyright (c) 2015–2016 Sebastian Mayr |
| ws | 7.5.13 | MIT | Copyright (c) 2011 Einar Otto Stangvik <einaros@gmail.com> |

## API (NuGet, çalışma zamanı) — 27 paket

| Paket | Sürüm | Lisans (nuspec) | Yazar (nuspec) | Telif (nuspec) |
|---|---|---|---|---|
| EFCore.NamingConventions | 10.0.1 | Apache-2.0 | Shay Rojansky | Copyright 2023 © Shay Rojansky |
| Humanizer.Core | 2.14.1 | MIT | Mehdi Khalili, Claire Novotny | Copyright (c) .NET Foundation and Contributors |
| Microsoft.AspNetCore.Identity.EntityFrameworkCore | 10.0.12 | MIT | Microsoft | © Microsoft Corporation. All rights reserved. |
| Microsoft.AspNetCore.OpenApi | 10.0.12 | MIT | Microsoft | © Microsoft Corporation. All rights reserved. |
| Microsoft.Build.Framework | 18.0.2 | MIT | Microsoft | © Microsoft Corporation. All rights reserved. |
| Microsoft.CodeAnalysis.Common | 5.0.0 | MIT | Microsoft | © Microsoft Corporation. All rights reserved. |
| Microsoft.CodeAnalysis.CSharp.Workspaces | 5.0.0 | MIT | Microsoft | © Microsoft Corporation. All rights reserved. |
| Microsoft.CodeAnalysis.CSharp | 5.0.0 | MIT | Microsoft | © Microsoft Corporation. All rights reserved. |
| Microsoft.CodeAnalysis.Workspaces.Common | 5.0.0 | MIT | Microsoft | © Microsoft Corporation. All rights reserved. |
| Microsoft.CodeAnalysis.Workspaces.MSBuild | 5.0.0 | MIT | Microsoft | © Microsoft Corporation. All rights reserved. |
| Microsoft.EntityFrameworkCore.Abstractions | 10.0.12 | MIT | Microsoft | © Microsoft Corporation. All rights reserved. |
| Microsoft.EntityFrameworkCore.Design | 10.0.12 | MIT | Microsoft | © Microsoft Corporation. All rights reserved. |
| Microsoft.EntityFrameworkCore.Relational | 10.0.12 | MIT | Microsoft | © Microsoft Corporation. All rights reserved. |
| Microsoft.EntityFrameworkCore | 10.0.12 | MIT | Microsoft | © Microsoft Corporation. All rights reserved. |
| Microsoft.Extensions.DependencyModel | 10.0.12 | MIT | Microsoft | © Microsoft Corporation. All rights reserved. |
| Microsoft.OpenApi | 2.12.0 | MIT | Microsoft | © Microsoft Corporation. All rights reserved. |
| Microsoft.VisualStudio.SolutionPersistence | 1.0.52 | MIT | Microsoft | © Microsoft Corporation. All rights reserved. |
| Mono.TextTemplating | 3.0.0 | MIT | Mikayla Hutchinson | belirtilmemiş |
| Newtonsoft.Json | 13.0.4 | MIT | James Newton-King | Copyright © James Newton-King 2008 |
| Npgsql.EntityFrameworkCore.PostgreSQL | 10.0.3 | PostgreSQL | Shay Rojansky,Austin Drenski,Yoh Deadfall | Copyright 2025 © The Npgsql Development Team |
| Npgsql | 10.0.3 | PostgreSQL | Shay Rojansky,Nikita Kazmin,Brar Piening,Nino Floris,Yoh Deadfall,Austin Drenski,Emil Lenngren,Francisco Figueiredo Jr.,Kenji Uno | Copyright 2025 © The Npgsql Development Team |
| System.CodeDom | 6.0.0 | MIT | Microsoft | © Microsoft Corporation. All rights reserved. |
| System.Composition.AttributedModel | 9.0.0 | MIT | Microsoft | © Microsoft Corporation. All rights reserved. |
| System.Composition.Convention | 9.0.0 | MIT | Microsoft | © Microsoft Corporation. All rights reserved. |
| System.Composition.Hosting | 9.0.0 | MIT | Microsoft | © Microsoft Corporation. All rights reserved. |
| System.Composition.Runtime | 9.0.0 | MIT | Microsoft | © Microsoft Corporation. All rights reserved. |
| System.Composition.TypedParts | 9.0.0 | MIT | Microsoft | © Microsoft Corporation. All rights reserved. |
