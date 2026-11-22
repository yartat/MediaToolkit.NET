#!/bin/bash
# Builds nasm, meson, x264, freetype, fribidi, harfbuzz, libass, libplacebo, FFmpeg (9.1 dev) and mpv into ~/ff91.
# Nothing outside $HOME is touched. Each stage leaves a marker and is skipped when it is there.
set -euo pipefail

P=$HOME/ff91
B=$HOME/ff91-build
LOG=$B/log
mkdir -p "$P" "$B" "$LOG" "$B/src"
export PATH="$P/bin:$P/meson:$PATH"
export PKG_CONFIG_PATH="$P/lib/pkgconfig:$P/lib/x86_64-linux-gnu/pkgconfig:$P/share/pkgconfig"
export LD_LIBRARY_PATH="$P/lib:$P/lib/x86_64-linux-gnu${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
J=$(nproc)

stage() { # name
  local name=$1
  if [ -e "$B/done-$name" ]; then echo "== $name: done already"; return 1; fi
  echo "== $name: building"; return 0
}
finish() { touch "$B/done-$1"; echo "== $1: ok"; }
fetch() { # url file
  [ -s "$B/src/$2" ] || curl -fL --retry 3 -o "$B/src/$2" "$1"
}
# The sources on D: have CRLF line endings (autocrlf), which breaks configure and every script with a shebang.
lf() { ( cd "$1" && grep -rIl --null "$(printf '\r')" . 2>/dev/null | xargs -0 -r sed -i "s/$(printf '\r')\$//" ); }
unpack() { # file dir
  rm -rf "$B/src/$2"; mkdir -p "$B/src/$2"; tar -xf "$B/src/$1" -C "$B/src/$2" --strip-components=1
}

if stage nasm; then
  fetch https://www.nasm.us/pub/nasm/releasebuilds/2.16.03/nasm-2.16.03.tar.xz nasm.tar.xz
  unpack nasm.tar.xz nasm
  ( cd "$B/src/nasm" && ./configure --prefix="$P" && make -j"$J" && make install ) >"$LOG/nasm.log" 2>&1
  finish nasm
fi

if stage meson; then
  fetch https://github.com/mesonbuild/meson/releases/download/1.9.1/meson-1.9.1.tar.gz meson.tar.gz
  unpack meson.tar.gz meson
  rm -rf "$P/meson"; cp -r "$B/src/meson" "$P/meson"
  printf '#!/bin/sh\nexec python3 "%s/meson/meson.py" "$@"\n' "$P" > "$P/bin/meson"; chmod +x "$P/bin/meson"
  meson --version >"$LOG/meson.log" 2>&1
  finish meson
fi

if stage x264; then
  [ -d "$B/src/x264-git" ] || git clone --depth 1 https://code.videolan.org/videolan/x264.git "$B/src/x264-git" >"$LOG/x264-clone.log" 2>&1
  ( cd "$B/src/x264-git" && ./configure --prefix="$P" --enable-shared --disable-cli && make -j"$J" && make install ) >"$LOG/x264.log" 2>&1
  finish x264
fi

if stage freetype; then
  fetch https://download.savannah.gnu.org/releases/freetype/freetype-2.13.3.tar.xz freetype.tar.xz
  unpack freetype.tar.xz freetype
  ( cd "$B/src/freetype" && ./configure --prefix="$P" --without-harfbuzz --without-png --without-brotli --without-bzip2 \
      && make -j"$J" && make install ) >"$LOG/freetype.log" 2>&1
  finish freetype
fi

if stage fribidi; then
  fetch https://github.com/fribidi/fribidi/releases/download/v1.0.16/fribidi-1.0.16.tar.xz fribidi.tar.xz
  unpack fribidi.tar.xz fribidi
  ( cd "$B/src/fribidi" && meson setup build --prefix="$P" --libdir=lib -Ddocs=false -Dtests=false -Dbin=false \
      && meson compile -C build && meson install -C build ) >"$LOG/fribidi.log" 2>&1
  finish fribidi
fi

if stage harfbuzz; then
  fetch https://github.com/harfbuzz/harfbuzz/releases/download/11.2.1/harfbuzz-11.2.1.tar.xz harfbuzz.tar.xz
  unpack harfbuzz.tar.xz harfbuzz
  ( cd "$B/src/harfbuzz" && meson setup build --prefix="$P" --libdir=lib -Dfreetype=enabled -Dglib=disabled -Dgobject=disabled \
      -Dcairo=disabled -Dicu=disabled -Dtests=disabled -Dintrospection=disabled -Ddocs=disabled -Dbenchmark=disabled -Dutilities=disabled \
      && meson compile -C build && meson install -C build ) >"$LOG/harfbuzz.log" 2>&1
  finish harfbuzz
fi

if stage libass; then
  fetch https://github.com/libass/libass/releases/download/0.17.4/libass-0.17.4.tar.xz libass.tar.xz
  unpack libass.tar.xz libass
  ( cd "$B/src/libass" && ./configure --prefix="$P" --disable-static --disable-fontconfig --disable-require-system-font-provider \
      && make -j"$J" && make install ) >"$LOG/libass.log" 2>&1
  finish libass
fi

if stage libplacebo; then
  [ -d "$B/src/libplacebo-git" ] || git clone --depth 1 --recursive --shallow-submodules https://code.videolan.org/videolan/libplacebo.git "$B/src/libplacebo-git" >"$LOG/libplacebo-clone.log" 2>&1
  ( cd "$B/src/libplacebo-git" && meson setup build --prefix="$P" --libdir=lib -Dvulkan=disabled -Dopengl=disabled -Dd3d11=disabled \
      -Dglslang=disabled -Dshaderc=disabled -Dlcms=disabled -Ddemos=false -Dtests=false -Dunwind=disabled -Dxxhash=disabled \
      && meson compile -C build && meson install -C build ) >"$LOG/libplacebo.log" 2>&1
  finish libplacebo
fi

if stage expat; then
  fetch https://github.com/libexpat/libexpat/releases/download/R_2_7_1/expat-2.7.1.tar.xz expat.tar.xz
  unpack expat.tar.xz expat
  ( cd "$B/src/expat" && ./configure --prefix="$P" --disable-static --without-docbook --without-examples --without-tests \
      && make -j"$J" && make install ) >"$LOG/expat.log" 2>&1
  finish expat
fi

if stage gperf; then
  fetch https://ftp.gnu.org/gnu/gperf/gperf-3.1.tar.gz gperf.tar.gz
  unpack gperf.tar.gz gperf
  ( cd "$B/src/gperf" && ./configure --prefix="$P" && make -j"$J" && make install ) >"$LOG/gperf.log" 2>&1
  finish gperf
fi

if stage fontconfig; then
  fetch https://www.freedesktop.org/software/fontconfig/release/fontconfig-2.16.0.tar.xz fontconfig.tar.xz
  unpack fontconfig.tar.xz fontconfig
  ( cd "$B/src/fontconfig" && ./configure --prefix="$P" --sysconfdir="$P/etc" --localstatedir="$P/var" --disable-static \
      --disable-docs --disable-nls --with-default-fonts=/usr/share/fonts --with-add-fonts=/usr/local/share/fonts \
      && make -j"$J" && make install ) >"$LOG/fontconfig.log" 2>&1
  finish fontconfig
fi

if stage libass2; then
  ( cd "$B/src/libass" && make distclean >/dev/null 2>&1 || true
    ./configure --prefix="$P" --disable-static --enable-fontconfig && make -j"$J" && make install ) >"$LOG/libass2.log" 2>&1
  finish libass2
fi

if stage lame; then
  fetch "https://sourceforge.net/projects/lame/files/lame/3.100/lame-3.100.tar.gz/download" lame.tar.gz
  unpack lame.tar.gz lame
  ( cd "$B/src/lame" && ./configure --prefix="$P" --disable-static --disable-frontend --disable-decoder \
      && make -j"$J" && make install ) >"$LOG/lame.log" 2>&1
  finish lame
fi

if stage opus; then
  fetch https://downloads.xiph.org/releases/opus/opus-1.5.2.tar.gz opus.tar.gz
  unpack opus.tar.gz opus
  ( cd "$B/src/opus" && ./configure --prefix="$P" --disable-static --disable-doc --disable-extra-programs \
      && make -j"$J" && make install ) >"$LOG/opus.log" 2>&1
  finish opus
fi

if stage lua; then
  fetch https://www.lua.org/ftp/lua-5.2.4.tar.gz lua.tar.gz
  unpack lua.tar.gz lua
  ( cd "$B/src/lua" && make posix MYCFLAGS="-fPIC" -j"$J" && make INSTALL_TOP="$P" install \
      && mkdir -p "$P/lib/pkgconfig" \
      && printf 'prefix=%s\nName: Lua\nDescription: Lua 5.2\nVersion: 5.2.4\nLibs: -L${prefix}/lib -llua -lm -ldl\nCflags: -I${prefix}/include\n' "$P" > "$P/lib/pkgconfig/lua5.2.pc" ) >"$LOG/lua.log" 2>&1
  finish lua
fi

if stage ffmpeg2; then
  rm -rf "$B/src/ffmpeg"; mkdir -p "$B/src/ffmpeg"
  tar -C /mnt/d/Work/My/Projects/Multimedia/FFmpeg --exclude=./.git --exclude=./graphify-out -cf - . | tar -C "$B/src/ffmpeg" -xf -
  lf "$B/src/ffmpeg"
  ( cd "$B/src/ffmpeg" && ./configure --prefix="$P" --enable-shared --disable-static --enable-gpl --enable-libx264 --enable-libmp3lame --enable-libopus --disable-doc \
      --extra-cflags="-I$P/include" --extra-ldflags="-L$P/lib" \
      && make -j"$J" && make install ) >"$LOG/ffmpeg.log" 2>&1
  finish ffmpeg2
fi

if stage mpv2; then
  rm -rf "$B/src/mpv"; mkdir -p "$B/src/mpv"
  tar -C /mnt/d/Work/My/Projects/Multimedia/mpv --exclude=./.git -cf - . | tar -C "$B/src/mpv" -xf -
  lf "$B/src/mpv"
  ( cd "$B/src/mpv" && meson setup build --prefix="$P" --libdir=lib -Dauto_features=disabled -Dgl=disabled -Dlibmpv=true -Dcplayer=true \
      -Dzlib=enabled -Diconv=disabled -Dlua=lua5.2 \
      && meson compile -C build && meson install -C build ) >"$LOG/mpv.log" 2>&1
  finish mpv2
fi

echo "ALL DONE"
"$P/bin/ffmpeg" -hide_banner -version | head -1
"$P/bin/mpv" --version | head -1
