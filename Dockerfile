# Build Layer
FROM ://microsoft.com AS build-env
WORKDIR /app

# Install native dependencies required by compiler components
RUN apt-get update && apt-get install -y clang zlib1g-dev

# Build and run target compilation
COPY *.csproj ./
RUN dotnet restore

COPY . ./
RUN dotnet publish -c Release -r linux-x64 -o out /p:PublishAot=true

# Minimal Production Layer
FROM alpine:3.19
WORKDIR /
RUN apk add --no-cache libgcc libstdc++
COPY --from=build-env /app/out/MandarinDacBridge /MandarinDacBridge

EXPOSE 55500
CMD ["/MandarinDacBridge"]
