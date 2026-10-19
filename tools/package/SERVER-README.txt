WHITE KNUCKLE MATCH SERVER  {VERSION}
====================================

The server pairs players, keeps the match clock and ratings, and passes positions and splits between
the two players. Players never connect to each other, only to this server.


RUNNING IT
----------
1. Double-click  start-server.bat . (Nothing else needs to be installed.)
   Windows Firewall may ask whether to allow it; allow it.
2. Players connect to  your-address:47600 . Open TCP port 47600 on your router
   (port forwarding) and point it at this PC.
3. So lobby codes work for friends, tell the server how they reach it: open start-server.bat in
   Notepad and fill in the PUBLIC_ADDRESS line, for example  set PUBLIC_ADDRESS=203.0.113.7:47600
   (your public IP, or a tunnel address such as one from playit.gg).

Players, ratings and finished matches are kept in the database  data\wkrace.db  (SQLite).
To encrypt it, put a long random key in a text file and add  --db-key-file <that file>  to the
server command in start-server.bat. Keep a copy of the key: without it the data cannot be read.

Run  WKRace.Server.exe --help  for every option (time limit, countdown, reconnect grace, TLS, ...).


NOTES
-----
- Traffic is not encrypted unless you set up TLS (--cert). Fine between friends; use TLS for a
  public server.
- Your PC is the server: keep it on and connected while people match.
