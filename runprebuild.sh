#!/bin/sh
mono bin/Prebuild.exe /target vs2008 /targetframework v3_5
git log --pretty=format:"Aurora (%cd.%h)" --date=short -n 1 > bin/.version