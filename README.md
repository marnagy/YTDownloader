# YTDownloader

## Description

This is implementation using **C#** and various libraries. It is **intended to be used in command-line interface** (CLI) and it can download publicly available videos from Youtube and extract audio and add metadata including thumbnail to it.

It also supports downloading of playlists under *-p/--playlist* command-line flag.

You can see all flags using flag *--help*.

It requires **.NET 5+** framework to run.
If you want to run this project with **.NET 6**, you will need to change tag *Project > PropertyGroup > TargetFramework* in file *YTDownloader.csproj* to *net6.0*.

# Work In Progress

Migrate to Docker to be used as a service.
