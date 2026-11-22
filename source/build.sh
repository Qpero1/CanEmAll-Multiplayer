#!/bin/bash
# usage: build.sh <srcdir> <out.dll> <lnldir>
D=$HOME/build/root/usr/lib/dotnet
SRC=$1; OUT=$2; LNL=$3
mapfile -t REFS < $HOME/build/refs.txt
ARGS=()
for r in "${REFS[@]}"; do ARGS+=("-r:$r"); done
DOTNET_ROOT=$D $D/dotnet $D/sdk/8.0.134/Roslyn/bincore/csc.dll -nologo -noconfig -nostdlib -target:library -unsafe -optimize -langversion:10 \
 '-define:TRACE;LITENETLIB_UNSAFE;NET5_0_OR_GREATER;NETCOREAPP3_0_OR_GREATER;NET6_0_OR_GREATER' -nowarn:1591,1701,1702,CS8632,CS0436 \
 -out:$OUT "${ARGS[@]}" $(for i in 0 1 2 3 4 5 6 7; do echo -n "-resource:$SRC/../res/cursor$i.bin,cursor$i -resource:$SRC/../res/cursorS$i.bin,cursorS$i "; done) -resource:$SRC/../res/hitmarker.pcm,hitmarker -resource:$SRC/../res/hitcross.bin,hitcross -resource:$SRC/../res/loc.tsv,loc $SRC/*.cs $(find $LNL -name '*.cs' | grep -v '/obj/' | grep -v -i test)
