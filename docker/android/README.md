# Android Docker Workflow

This container provides the .NET 10 SDK, JDK 21, the Android SDK command-line tools,
the Android emulator package, Android platform APIs 35 and 36, and the system image expected by `scripts/android-emulator.sh`.

## Build the image

```bash
docker compose -f docker-compose.android.yml build
```

## Run commands directly with compose

```bash
docker compose -f docker-compose.android.yml run --rm android publish
```

Debug examples:

```bash
docker compose -f docker-compose.android.yml run --rm android publish
docker compose -f docker-compose.android.yml run --rm android install
docker compose -f docker-compose.android.yml run --rm android run
docker compose -f docker-compose.android.yml run --rm android deploy
docker compose -f docker-compose.android.yml run --rm android emulator
docker compose -f docker-compose.android.yml run --rm android shell
```

Release examples:

```bash
docker compose -f docker-compose.android.yml run --rm -e CONFIGURATION=Release android publish
docker compose -f docker-compose.android.yml run --rm -e CONFIGURATION=Release android deploy
```

`CONFIGURATION` defaults to `Debug`, so only pass `-e CONFIGURATION=Release` when you want a release APK.

## Use the host emulator from the container

The compose file uses `network_mode: host`, so `adb` inside the container talks to the
same ADB server/network namespace as the host.

So the intended flow is:

1. Start the emulator on the host
2. Run `android deploy` from compose
3. The container publishes, installs, and launches the app automatically

## Start the emulator inside the container

This is only practical if your Docker setup supports nested virtualization and GUI forwarding.
If you want to try it:

```bash
bash scripts/android-emulator.sh --ensure
```

Most Linux setups will have a better experience running the emulator on the host and letting
the container talk to it over ADB.
