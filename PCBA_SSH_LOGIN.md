# PCBA SSH Login

## Target

- Host: `192.168.0.74`
- User: `originflow`
- Board project: `/home/originflow/work/spaceTest3576`
- Client: Git Bash bundled with Git for Windows

## Password Askpass Setup

Create this file outside the repository:

`C:\Users\31239\AppData\Local\Temp\pcba_ssh_askpass.sh`

```sh
#!/bin/sh
printf '%s\\n' '<BOARD_PASSWORD>'
```

Run in Git Bash:

```sh
chmod 700 /c/Users/31239/AppData/Local/Temp/pcba_ssh_askpass.sh
```

Do not commit this file or replace `<BOARD_PASSWORD>` with a real password in this document.

## Automatic SSH Login

Run in PowerShell:

```powershell
& 'C:\Program Files\Git\bin\bash.exe' -lc "export DISPLAY=codex:0 SSH_ASKPASS=/c/Users/31239/AppData/Local/Temp/pcba_ssh_askpass.sh SSH_ASKPASS_REQUIRE=force; ssh -o StrictHostKeyChecking=accept-new -o PreferredAuthentications=password -o PubkeyAuthentication=no originflow@192.168.0.74"
```

## Execute A Board Command

```powershell
& 'C:\Program Files\Git\bin\bash.exe' -lc "export DISPLAY=codex:0 SSH_ASKPASS=/c/Users/31239/AppData/Local/Temp/pcba_ssh_askpass.sh SSH_ASKPASS_REQUIRE=force; ssh -o StrictHostKeyChecking=accept-new -o PreferredAuthentications=password -o PubkeyAuthentication=no originflow@192.168.0.74 'cd /home/originflow/work/spaceTest3576 && make'"
```

## Copy A File To The Board

```powershell
& 'C:\Program Files\Git\bin\bash.exe' -lc "export DISPLAY=codex:0 SSH_ASKPASS=/c/Users/31239/AppData/Local/Temp/pcba_ssh_askpass.sh SSH_ASKPASS_REQUIRE=force; scp -o StrictHostKeyChecking=accept-new -o PreferredAuthentications=password -o PubkeyAuthentication=no ./local-file originflow@192.168.0.74:/home/originflow/work/spaceTest3576/"
```

## Host Key File

The first connection may add the board fingerprint to:

`C:\Users\31239\.ssh\known_hosts`

For long-term use, prefer SSH public-key authentication instead of storing a plaintext password in an askpass script.
