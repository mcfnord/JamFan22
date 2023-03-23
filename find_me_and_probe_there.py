import os
import time
import json
import urllib.request
import hashlib

scan_endpoint = [
    'http://143.198.104.205/servers.php?central=anygenre1.jamulus.io:22124',
    'http://143.198.104.205/servers.php?central=anygenre2.jamulus.io:22224',
    'http://143.198.104.205/servers.php?central=anygenre3.jamulus.io:22624',
    'http://143.198.104.205/servers.php?central=rock.jamulus.io:22424',
    'http://143.198.104.205/servers.php?central=jazz.jamulus.io:22324',
    'http://143.198.104.205/servers.php?central=classical.jamulus.io:22524',
    'http://143.198.104.205/servers.php?central=choral.jamulus.io:22724',
]

# this script runs on jf, which doesn't have a probe onboard.

probe = ""
isLoungeFree = str(urllib.request.urlopen("http://lounge.jamulus.live/free.txt").readline().decode('utf-8').strip())
if isLoungeFree == "True":
    probe = "lounge"
else:
    isRadioFree = str(urllib.request.urlopen("http://radio.jamulus.live/free.txt").readline().decode('utf-8').strip())
    if isRadioFree == "True":
        probe = "radio"

if probe == "":
    print("no probes available.")
    exit()

print("using " + probe + ".jamulus.live recklessly.")

for url in scan_endpoint:
    data = json.loads(urllib.request.urlopen(url).read())
    for server in data:
        if 'clients' in server:
            for client in server['clients']:
                bytes = (client['name'] + client['country'] + client['instrument']).encode('utf-8')
                theirguid = hashlib.md5(bytes).hexdigest()

                if 'd7da0b649b4c4f01bfbceed3bb9008fa' == theirguid:
                    where_john_is = server['ip'] + ":" + str(server['port'])

                    denylist = str(urllib.request.urlopen("https://jamulus.live/cannot-dock.txt").read())
                    if denylist.find(where_john_is) != -1:      # on denylist.
                        print("This destination is already on the denylist.")
                        exit()
                    else:
                        allowlist = str(urllib.request.urlopen("https://jamulus.live/can-dock.txt").read())
                        if allowlist.find(where_john_is) != -1: # on allowlist.
                            print("this destination is already on the allowlist.")
                            exit()
                        else:

                            # is there already a lobby there? that breaks my test
                            occupants = str(urllib.request.urlopen("http://143.198.104.205/servers.php?server=" + where_john_is).read())
                            if occupants.find("obby") == -1:        # no lobby there. ok to try one.

                                # Request a probe by writing my location on the lounge server.
                                DEPLOYMENT_REQUEST_FILE = "/root/JamFan22/JamFan22/wwwroot/requested_on_" + probe + ".txt"
                                os.system("sudo sh -c 'echo " + where_john_is + " > " + DEPLOYMENT_REQUEST_FILE + "'")
                                time.sleep(20) # wait for the probe to arrive
                                didlobbyarrive = str(urllib.request.urlopen("http://143.198.104.205/servers.php?server=" + where_john_is).read())
                                if didlobbyarrive.find("obby") != -1:
                                    print(where_john_is + " probe succeeded.")
                                    os.system("sudo sh -c 'echo " + where_john_is + " >> /root/JamFan22/JamFan22/wwwroot/can-dock.txt'")    # add to allowlist
                                    os.system("sudo sh -c 'grep -v '" + probe + ".jamulus' /root/JamFan22/JamFan22/wwwroot/map.txt > /root/JamFan22/JamFan22/wwwroot/map.txt") # erase from map
                                    os.system("sudo sh -c 'echo " + where_john_is + " https://" + probe + ".jamulus.live >> /root/JamFan22/JamFan22/wwwroot/map.txt'") # add to map
                                else:
                                    os.system("sudo sh -c 'echo " + where_john_is + " >> /root/JamFan22/JamFan22/wwwroot/cannot-dock.txt'") # add to denylist
                                    print(where_john_is + " probe failed.")
                                    os.system("sudo sh -c 'echo 127.0.0.1 > " + DEPLOYMENT_REQUEST_FILE + "'") # don't leave this probe hanging
                            exit()

