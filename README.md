# Octockup

[![License](https://badgen.net/github/license/bvdcode/octockup)](LICENSE)
[![CI](https://github.com/bvdcode/octockup/actions/workflows/docker-image.yml/badge.svg)](https://github.com/bvdcode/octockup/actions)
[![CodeFactor](https://www.codefactor.io/repository/github/bvdcode/octockup/badge)](https://www.codefactor.io/repository/github/bvdcode/octockup)
[![Release](https://badgen.net/github/release/bvdcode/octockup?label=version)](https://github.com/bvdcode/octockup/releases)
[![Docker Pulls](https://badgen.net/docker/pulls/bvdcode/octockup?icon=docker&label=pulls)](https://hub.docker.com/r/bvdcode/octockup)
[![Docker Image Size](https://badgen.net/docker/size/bvdcode/octockup?icon=docker&label=size)](https://hub.docker.com/r/bvdcode/octockup)
[![Github last-commit](https://img.shields.io/github/last-commit/bvdcode/octockup)](https://github.com/bvdcode/octockup/commits/main/)

> Live: [octockup.splidex.com](https://octockup.splidex.com)

Octockup is an autobackup application that ships its backend and frontend in one Docker container and stores metadata in PostgreSQL. It allows you to gather and manage data from various sources, such as YouTube, SSH, FTP, Email, and more, directly through the browser.

<img width="820" height="319" alt="image" src="https://github.com/user-attachments/assets/9cdaf805-5b28-4a3c-8621-88b5dd847278" />


---

## Key Features

- **Application Container:** The backend and frontend ship together as one image.
- **Backend and Frontend:** Full integration of backend and frontend for simplified deployment.
- **Incremental Backups:** Save only the necessary changes with each backup.
- **Connecting Various Sources:** You can connect YouTube, SSH, FTP, and many other sources to gather data.
- **Web Interface:** User-friendly web interface for managing all application functions.
- **Data Deduplication:** Efficient storage usage by avoiding duplicate data.
- **Encryption:** All backup data is encrypted before leaving the server using AES-GCM.
- **PostgreSQL Metadata Store:** Uses PostgreSQL for durable application and backup metadata.
- **External Authentication:** Supports account-linked OpenID Connect providers and administrator-managed users.

---

## Portable backups

Create a backup with an optional separate master key to share it without sharing the server key. Existing backups keep their current key. Chunks encrypted with a separate key are isolated from other backups; deduplication continues within that backup.

Completed snapshots of backups with a separate key publish an encrypted, paged catalog into the target automatically. Wait for catalog publication before handing over the target. Existing backups can publish their catalogs manually from **Storage Browser** without uploading file contents again.

On another instance, configure the target, open **Storage Browser**, and enter the backup key. No source database import is required. The read-only viewer supports snapshot selection, folder browsing, file downloads, and streaming folder ZIP downloads. File catalogs are paged in groups of 500 entries instead of loading a complete snapshot into memory.

Viewing keys are retained in server memory for a session lasting up to 30 minutes. **Lock and exit** closes access sooner. Keep the separate backup key: the receiving instance's server key cannot replace it. Use a dedicated target or prefix for a shared backup, and give the recipient read-only storage credentials when possible.

## Installation

Dockerhub: [Link](https://hub.docker.com/r/bvdcode/octockup)

1. Make sure you have Docker and Docker Compose installed.
2. Create `docker-compose.yml` file:

```yaml
services:
  postgres:
    image: postgres:17-alpine
    environment:
      - POSTGRES_DB=octockup
      - POSTGRES_USER=octockup_client
      - POSTGRES_PASSWORD=${OCTOCKUP_DB_PASS}
    volumes:
      - octockup-postgres:/var/lib/postgresql/data
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U octockup_client -d octockup"]
      interval: 5s
      timeout: 5s
      retries: 10

  octockup:
    image: bvdcode/octockup:latest
    depends_on:
      postgres:
        condition: service_healthy
    ports:
      - 8080:8080
    environment:
      # Required: 32 chars master key for encrypting everything
      - OCTOCKUP_MASTER_KEY=${OCTOCKUP_MASTER_KEY}

      # Required: PostgreSQL connection
      - OCTOCKUP_POSTGRES_HOST=postgres
      - OCTOCKUP_POSTGRES_PORT=5432
      - OCTOCKUP_POSTGRES_DATABASE=octockup
      - OCTOCKUP_POSTGRES_USERNAME=octockup_client
      - OCTOCKUP_POSTGRES_PASSWORD=${OCTOCKUP_DB_PASS}
    volumes:
      - /data/octockup:/app/data
      # Mounts to backup if needed:
      - /files:/app/data/mounts/files:ro
      - /apps:/app/data/mounts/apps:ro

volumes:
  octockup-postgres:
```

3. Start the application using Docker Compose:

```bash
docker compose up -d
```

4. First login

---

## Usage

1. Open your browser and navigate to the address where the application is running.
2. Log in and set up connections to the necessary data sources (YouTube, SSH, FTP, etc.).
3. Start gathering and managing data using the user-friendly web interface.

### External authentication

The first password login creates the initial administrator. An administrator can then:

1. Add and enable an OpenID Connect provider in **Settings** and register the callback URL shown there with the provider.
2. Link each existing Octockup account to its external account from **Connected Accounts**.
3. Disable password login after every active user has an account linked to an enabled provider.

OIDC sign-in never creates or links users automatically. Password login can only be disabled while every active user retains an enabled external sign-in method.

---

## Configuration

### Configuration Files

- **`docker-compose.yml`** - Docker Compose configuration for managing the container.

### Environment variables

```text
OCTOCKUP_MASTER_KEY: 32-character master key for encrypting sensitive data.
OCTOCKUP_POSTGRES_HOST: PostgreSQL host.
OCTOCKUP_POSTGRES_PORT: PostgreSQL port (default: 5432).
OCTOCKUP_POSTGRES_DATABASE: PostgreSQL database name.
OCTOCKUP_POSTGRES_USERNAME: PostgreSQL username.
OCTOCKUP_POSTGRES_PASSWORD: PostgreSQL password.
```

---

## 🏗 Architecture Overview

```mermaid
flowchart LR
    subgraph SRC[Backup Source]
        S1[SFTP]
        S2[IMAP]
        S3[File System]
        S4[Other]
    end

    subgraph CORE[Octockup Core]
        B[Backup Job]
        SNAP[Snapshot]
        F[SnapshotFile]
        CH[Chunker]
        H[SHA-256 Hashing]
        IDX[UploadedHash<br/>Global Dedup Index]
        ENC[AES-GCM Encryption]
        CMP[Brotli Compression]
    end

    subgraph STOR[Backup Storage]
        T1[S3]
        T2[SFTP]
        T3[Other Storage]
    end

    SRC --> B
    B --> SNAP
    SNAP --> F
    F --> CH
    CH --> H
    H --> IDX
    IDX -->|exists| SNAP
    H -->|new| CMP --> ENC --> STOR

```

**High-level flow:**

1. A **Source** provides a hierarchical list of files.
2. A **Backup Job** scans files and creates a new **Snapshot**.
3. Each file becomes a **SnapshotFile**.
4. Files are split into fixed-size **chunks**.
5. Each chunk is hashed (SHA-256).
6. Hashes are checked against the global **UploadedHash** table.
7. Only **missing chunks** are uploaded to the **Storage**.
8. The Snapshot stores only **references to chunk hashes**, not raw data.

This allows:

- true **block-level deduplication**
- **incremental backups**
- efficient storage usage across all snapshots

---

## 🧱 How Deduplication Works

Octockup uses **content-addressed chunk-level deduplication**.

1. Every file is split into fixed-size chunks.
2. Each chunk is hashed using **SHA-256**.
3. Before uploading a chunk, Octockup checks the `UploadedHash` table:

   - `(ModuleId + Hash)` must be unique.

4. If the hash already exists:

   - the chunk is **NOT uploaded again**
   - only a reference is stored in `SnapshotFile.ChunkHashes`.

5. If the hash does not exist:

   - the chunk is **compressed**
   - **encrypted**
   - uploaded to the storage
   - recorded in `UploadedHash`.

As a result:

- identical files across different backups are stored **only once**
- even partial file changes reuse unchanged blocks
- storage usage grows **only with truly new data**

Deduplication works **globally per storage**, not only per snapshot.

---

## 🔐 How Encryption Works

All backup data is encrypted **before leaving the server**.

1. A global **MASTER_KEY** (32 bytes) is provided via environment variable.
2. Every chunk is encrypted using:

   - **AES-GCM**
   - streamed encryption (no full-buffer loading)

3. The encryption pipeline is:

```
Raw Chunk → Brotli Compression → AES-GCM Encryption → Upload
```

4. Encryption is performed **on the fly during chunk streaming**.
5. The storage backend **never sees plaintext data**.
6. During download:

   - encrypted chunks are fetched
   - decrypted and decompressed in memory
   - reassembled into the original file stream.

Security guarantees:

- All data at rest in storage is **encrypted**
- Authentication is provided by **GCM tags**
- Tampered chunks are **automatically rejected**
- The storage provider **cannot read backups**

The master key is **never stored** in the database and must be backed up securely.

---

## Updating

To update to the latest version of the application, follow these steps:

1. Update the image:
   ```bash
   docker compose pull
   ```
2. Restart the application:
   ```bash
   docker compose up -d
   ```

## Support

If you have any questions or issues, please create a new issue on GitHub or contact me via email:

octockup-github-support@belov.us

---
