"""Fetches what e2e_connect.py runs: Temurin JDK 17 and Kafka 3.9.1 (Scala 2.12) for Windows, from
the same places as msi-agent/tools/download.pl, checked against the published checksums.

    python fetch_connect.py <dir>

Run it before the test, while the machine still has internet. Prints the two directories to
pass to e2e_connect.py.
"""
import hashlib
import json
import os
import sys
import tarfile
import urllib.request
import zipfile

KAFKA = "kafka_2.12-3.9.1"
KAFKA_URL = f"https://archive.apache.org/dist/kafka/3.9.1/{KAFKA}.tgz"
TEMURIN_API = ("https://api.adoptium.net/v3/assets/latest/17/hotspot"
               "?architecture=x64&image_type=jdk&os=windows&vendor=eclipse")


def get(url):
    # The Adoptium API answers 403 to urllib's default User-Agent.
    request = urllib.request.Request(url, headers={"User-Agent": "sorcerer-e2e-fetch/1"})
    with urllib.request.urlopen(request, timeout=120) as r:
        return r.read()


def download(url, path, digest, expected):
    if not os.path.exists(path):
        print(f"downloading {url}", flush=True)
        data = get(url)
        with open(path + ".part", "wb") as f:
            f.write(data)
        os.replace(path + ".part", path)
    with open(path, "rb") as f:
        actual = hashlib.new(digest, f.read()).hexdigest()
    if actual != expected.lower():
        sys.exit(f"{path}: {digest} {actual}, expected {expected}")
    print(f"{os.path.basename(path)}: {digest} ok", flush=True)


def main():
    out = os.path.abspath(sys.argv[1])
    os.makedirs(out, exist_ok=True)

    # Kafka's .sha512 is gpg --print-md output: "<file>: 1A2B 3C4D ..." over several lines.
    sha512 = "".join(get(KAFKA_URL + ".sha512").decode().split(":", 1)[1].split())
    tgz = os.path.join(out, KAFKA + ".tgz")
    download(KAFKA_URL, tgz, "sha512", sha512)
    if not os.path.isdir(os.path.join(out, KAFKA)):
        with tarfile.open(tgz) as t:
            t.extractall(out, filter="data")

    asset = json.loads(get(TEMURIN_API))[0]
    package = asset["binary"]["package"]
    zip_path = os.path.join(out, package["name"])
    download(package["link"], zip_path, "sha256", package["checksum"])
    with zipfile.ZipFile(zip_path) as z:
        top = z.namelist()[0].split("/")[0]
        if not os.path.isdir(os.path.join(out, top)):
            z.extractall(out)

    print(f"jdk:   {os.path.join(out, top)}  ({asset['version']['openjdk_version']})")
    print(f"kafka: {os.path.join(out, KAFKA)}")


if __name__ == "__main__":
    main()
