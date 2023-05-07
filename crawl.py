import os
import time
import json
import urllib.request

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

print("only run this during severe lulls. quit now if it's not a lull.")
time.sleep(5)

while True:
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

    # since i can't run the probe for a long time, i'll just do one probe and quit.

    for url in scan_endpoint:
        data = json.loads(urllib.request.urlopen(url).read())
        for server in data:
#            WE EVEN CRAWL POPULATED SERVERS, HENCE WE ONLY RUN DURING SEVERE LULL
#            if 'clients' in server:
#                continue

            # we found a server that has no clients on it right now.
            ipPort = server['ip'] + ":" + str(server['port'])

            # is it on the denylist?
            denylist = str(urllib.request.urlopen("https://jamulus.live/cannot-dock.txt").read())
            if denylist.find(ipPort) != -1:      # on denylist.
                continue

            # is it on the allowlist?
            allowlist = str(urllib.request.urlopen("https://jamulus.live/can-dock.txt").read())
            if allowlist.find(ipPort) != -1: # on allowlist.
                continue

            # Request a probe by writing my location on the lounge server.
            print("Probing " + ipPort + " aka " + server['name'])
            DEPLOYMENT_REQUEST_FILE = "/root/JamFan22/JamFan22/wwwroot/requested_on_" + probe + ".txt"
            os.system("sudo sh -c 'echo " + ipPort + " > " + DEPLOYMENT_REQUEST_FILE + "'")

            time.sleep(40) # wait for the probe to arrive. wait a super long time.

            didlobbyarrive = str(urllib.request.urlopen("http://143.198.104.205/servers.php?server=" + ipPort).read())
            if didlobbyarrive.find("obby") != -1:
                print("probe succeeded.")
                os.system("sudo sh -c 'echo " + ipPort + " >> /root/JamFan22/JamFan22/wwwroot/can-dock.txt'")    # add to allowlist
                # os.system("sudo sh -c 'echo 127.0.0.1 > " + DEPLOYMENT_REQUEST_FILE + "'") # don't leave this probe hanging
            else:
                print("probe failed.")
                os.system("sudo sh -c 'echo " + ipPort + " >> /root/JamFan22/JamFan22/wwwroot/cannot-dock.txt'") # add to denylist
            break
    exit() # time.sleep(60) # just unclear about speed.
