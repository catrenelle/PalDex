FROM python:3.13-slim

# git: needed by pip to install palsav-flex/palooz from a git+https URL.
# openssh-client + rsync: needed by backend/remote.py's Linux pull path.
# build-essential: palooz builds a native C++ extension (Oodle/"ooz"
# decompression) from source on install — fails with "g++: No such file or
# directory" on python:3.13-slim without it.
RUN apt-get update && apt-get install -y --no-install-recommends \
    git openssh-client rsync build-essential \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app

COPY backend/requirements.txt backend/requirements.txt
RUN pip install --no-cache-dir -r backend/requirements.txt

COPY backend/ backend/
COPY frontend/ frontend/
COPY data/ data/

# Static extraction data (schematics_static.json etc.) + frontend/assets
# icons ship baked into the image from the Windows-only CUE4Parse extractor
# run — see extractor/PalExtract. Rerun that locally and rebuild the image
# to pick up changes; the extractor itself isn't part of this image.

EXPOSE 5151
CMD ["python", "backend/server.py"]
