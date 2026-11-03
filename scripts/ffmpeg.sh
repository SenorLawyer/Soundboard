#!/bin/sh
# Build the standalone import helper from the unmodified FFmpeg release.
set -eu
cd "$1"
./configure --target-os=mingw32 --arch=x86_64 --disable-everything --disable-autodetect --disable-network --disable-doc \
    --disable-debug --disable-x86asm --disable-avdevice --disable-swscale \
    --disable-ffplay --disable-ffprobe --enable-small --enable-ffmpeg \
    --enable-protocol=file,pipe --enable-muxer=pcm_s16le,null --enable-encoder=pcm_s16le \
    --enable-filter=aresample,aformat,anull,atrim,apad,pan,ebur128,volume,alimiter \
    --enable-demuxer=wav,w64,mp3,aac,loas,flac,ogg,mov,matroska,avi,asf,aiff,caf,au,wv,ape,ac3,eac3,dts,amr,tta,mpegps,mpegts,flv,mpegvideo,m4v,h264,hevc \
    --enable-parser=aac,aac_latm,ac3,dca,flac,mpegaudio,opus,vorbis,mpegvideo,h264,hevc,mpeg4video \
    --enable-decoder='pcm_*,adpcm_*,mp1,mp2,mp3,aac,aac_latm,alac,flac,opus,vorbis,wmav1,wmav2,wmapro,wmalossless,wavpack,ape,ac3,eac3,dca,amrnb,amrwb,tta,nellymoser'
make -j"$2" ffmpeg.exe
