# Minimal integration

LuckyCapture builds against unmodified `nitr57/pins` `unstable`.
No runtime camera/SDK/mediator source patch is required. The former core-lease
and native-driver experiments were stashed privately, outside active source.

The PINS wrapper calls the shared `build-single-package.yml` helper. It registers
the project/repository, supplies the source root, excludes duplicate core
assemblies and declares `pins >= 3.3.0.1066` and `ffmpeg` dependencies.
There is no capture-lease virtual package dependency or separate packager.

The TNS plugin adds a small hosted controller using public IMessageBroker
messages. The frontend is a conventional TNS plugin. GitHub workflows have not
run: the new remote is empty and all coordinated changes remain local.
