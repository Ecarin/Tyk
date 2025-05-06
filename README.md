# Tyk
**Track every second.**

[![.NET](https://img.shields.io/badge/.NET-9.0-purple)](https://dotnet.microsoft.com/)  
[![License: AGPL v3](https://img.shields.io/badge/License-AGPL--3.0-blue)](LICENSE)

A sleek, Telegram-based time-tracker with a Web API backend—perfect for logging tasks, generating reports, and keeping your productivity on point.

---

## Table of Contents

1. [Features](#features)
2. [Architecture](#architecture)
3. [Prerequisites](#prerequisites)
4. [Getting Started](#getting-started)
    - [Clone & Build](#clone--build)
    - [Configuration](#configuration)
    - [Migrations & Database](#migrations--database)
    - [Run Locally](#run-locally)
    - [Docker](#docker)
5. [Project Structure](#project-structure)
6. [Contributing](#contributing)
7. [Contact](#contact)
8. [License](#license)

---

## Features

- **Telegram Bot interface**: start, stop, pause, resume, list & report time entries
- **ASP.NET Core Web API** for CRUD operations & integration
- **Entity Framework Core** with Repository pattern for data access
- **Docker-ready**: containerize both API & Bot in one image
- **Configurable** via environment variables or `appsettings.json`

---

## Architecture

```
┌──────────────────┐       ┌───────────────────────────┐
│  Telegram Client │ <---> │  TelegramBotService       │
└──────────────────┘       ├───────────────────────────┤
                           │  ASP.NET Core Web API     │
┌──────────────────┐       ├───────────────────────────┤
│   HTTP Client    │ <---> │  TimeEntriesController    │
└──────────────────┘       │                           │
                           │  EF Core ── Repo → DB     │
                           └───────────────────────────┘
```

---

## Prerequisites

- **.NET 9.0 SDK** (or later)
- **SQLite** (or any EF Core–supported database)
- **Telegram Bot Token** (create via [@BotFather](https://t.me/BotFather))
- **Docker** (optional, for containerization)

---

## Getting Started

### Clone & Build

```bash
git clone https://github.com/Ecarin/tyk.git
cd tyk
dotnet build
```

### Configuration

Create an `appsettings.json` (or set environment variables) with at least:

```jsonc
{
  "Telegram": {
    "BotToken": "<YOUR_TELEGRAM_BOT_TOKEN>"
  },
  "ConnectionStrings": {
    "TimeTrackerDb": "Server=.;Database=TykDb;Trusted_Connection=True;"
  }
}
```

### Migrations & Database

```bash
dotnet tool install --global dotnet-ef
dotnet ef migrations add InitialCreate --project src/
dotnet ef database update --project src/
```

### Run Locally

```bash
dotnet run --project src/
```

### Docker

```bash
docker build -t tyk:latest .
docker run -d \\
  -e TELEGRAM__BOTTOKEN="<YOUR_TOKEN>" \\
  -e ConnectionStrings__TimeTrackerDb="<YOUR_CONN_STRING>" \\
  -p 5000:80 tyk:latest
```

---

## Project Structure

```
src/
├── Tyk.Api/
│   ├── Controllers/TimeEntriesController.cs
│   ├── Program.cs
│   └── Dockerfile
├── Tyk.Application/
│   ├── Interfaces/ITimeEntryRepository.cs
├── Tyk.Domain/
│   ├── Entities/TimeEntry.cs
│   └── Entities/ChatMessage.cs
├── Tyk.Infrastructure/
│   ├── Data/TimeTrackerContext.cs
│   └── Repositories/TimeEntryRepository.cs
└── Tyk.Api/.env.sample
```

---

## Contributing

Your improvements and fixes bring a smile—feel free to:

1. **Fork** this repository
2. **Create** a branch for your idea (`git checkout -b improve-awesome`)
3. **Commit** your enhancements (`git commit -m "🛠 description of change"`)
4. **Push** and **open a pull request**
5. We’ll review and merge—thank you for making Tyk better!

---

## Contact

- **Amin Ansari**
- ✉️ Email: [3carin@gmail.com](mailto:3carin@gmail.com)
- 💬 Telegram: [@ecarin](https://t.me/ecarin)

---

## License

This project is licensed under the **GNU AGPL v3.0**. See [LICENSE](LICENSE) for details.
