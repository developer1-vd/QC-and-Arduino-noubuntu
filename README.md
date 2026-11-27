# The Qualcomm and Arduino no Ubuntu Linux project

> NOTE! This isn't affiliated with Qualcomm or Arduino, this is a personal project (unofficial).
# VFlasher
Welcome to the Qualcomm and Arduino without Ubuntu project!
This uses a custom flasher called VFlasher made by me! To use it run: `sudo ./vflasher <board-manufacturer (e.g. qualcomm)> <board-name (e.g. yun)> <os (e.g. fedora)>` or use the [Windows GUI](windows/).
To install VFlasher locally so that you can type just `vflasher` anywhere, run `mkdir -p $HOME/.bin && mv /path/to/your/vflasher $HOME/.bin` and add $HOME/.bin to your PATH in `.bashrc` on UNIX/Linux/macOS/BSD, or on Windows, use the setup wizard for the Windows GUI. 

Run `./vflasher --list` to see the available images and `./vflasher --help` for all options. Images are described in the [`images/`](images/) folder; VFlasher downloads each image from its official source and checks its SHA-256 before writing anything.

Currently supported:

| Manufacturer | Board | OS | How it is flashed |
|---|---|---|---|
| qualcomm | [iq9075-evk](images/qualcomm/iq9075-evk/fedora/) | fedora | to the on-board UFS over USB in EDL mode (Linux) |
| qualcomm | [iq9075-evk](images/qualcomm/iq9075-evk/fedora-workstation/) | fedora-workstation (GNOME) | to the on-board UFS over USB in EDL mode (Linux) |
| arduino | [ventuno-q](images/arduino/ventuno-q/fedora/) | fedora | to the on-board eMMC over USB-C in EDL mode (Linux or Windows) |

---

## OSes
The *planned* OSes are:
1. Fedora
2. Arch Linux
3. Stock (and GUI) Debian
4. NixOS
5. Chimera Linux
6. openSUSE (both stable and rolling)
7. (maybe) Windows 10/11
8. BSD
 
---

## Goal
To add an alternate OS to Qualcomm and Arduino devices
