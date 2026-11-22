#!/bin/bash
# Runs the tests in WSL against the FFmpeg 9.1 and mpv 0.42 built into ~/ff91.
# SYSTEM_LIBS=1 leaves them out, for the tests of backends whose plug-ins link the system's libraries (GStreamer):
# the libraries in ~/ff91/lib (harfbuzz, freetype, libass...) would shadow the system's.
set -e
P=$HOME/ff91
if [ -z "$SYSTEM_LIBS" ]; then
  export PATH="$P/bin:$PATH"
  export LD_LIBRARY_PATH="$P/lib"
  export MEDIATOOLKITNET_FFMPEG_DIR="$P/lib"
fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
SRC=/mnt/d/Work/My/Projects/Multimedia/MediaToolkit.NET
DST=$HOME/mtn
mkdir -p "$DST"
tar -C "$SRC" --exclude=./.git --exclude=bin --exclude=obj --exclude=artifacts --exclude=.vs --exclude=graphify-out --exclude=.codegraph -cf - . | tar -C "$DST" -xf -
cd "$DST"
dotnet test "$@" --nologo 2>&1 | tail -${TAIL:-25}
