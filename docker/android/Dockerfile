FROM mcr.microsoft.com/dotnet/sdk:10.0

ARG DEBIAN_FRONTEND=noninteractive

RUN apt-get update && apt-get install -y --no-install-recommends \
    openjdk-21-jdk \
    wget \
    unzip \
    git \
    bash \
    fish \
    libglu1-mesa \
    libpulse0 \
    libnss3 \
    libxcomposite1 \
    libxcursor1 \
    libxi6 \
    libxkbcommon0 \
    libxrandr2 \
    libxdamage1 \
    libxfixes3 \
    libgbm1 \
    libasound2t64 \
    adb \
    && rm -rf /var/lib/apt/lists/*

ENV JAVA_HOME=/usr/lib/jvm/java-21-openjdk-amd64
ENV ANDROID_HOME=/opt/android-sdk
ENV ANDROID_SDK_ROOT=/opt/android-sdk
ENV PATH=${JAVA_HOME}/bin:${ANDROID_HOME}/cmdline-tools/latest/bin:${ANDROID_HOME}/platform-tools:${ANDROID_HOME}/emulator:${PATH}

RUN mkdir -p ${ANDROID_HOME}/cmdline-tools

RUN wget -q https://dl.google.com/android/repository/commandlinetools-linux-13114758_latest.zip -O /tmp/cmdline-tools.zip \
    && unzip -q /tmp/cmdline-tools.zip -d /tmp/android-cmdline \
    && rm -rf ${ANDROID_HOME}/cmdline-tools/latest \
    && mv /tmp/android-cmdline/cmdline-tools ${ANDROID_HOME}/cmdline-tools/latest \
    && rm -rf /tmp/cmdline-tools.zip /tmp/android-cmdline

RUN yes | sdkmanager --sdk_root=${ANDROID_HOME} --licenses

RUN sdkmanager --sdk_root=${ANDROID_HOME} \
    "platform-tools" \
    "build-tools;35.0.0" \
    "platforms;android-35" \
    "platforms;android-36" \
    "emulator" \
    "system-images;android-36;google_apis;x86_64"

RUN dotnet workload install android

WORKDIR /workspace

ENTRYPOINT ["/bin/bash", "/workspace/docker/android/android-tool.sh"]
CMD ["help"]
