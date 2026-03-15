# ORBIT-Pure Beta Tester Onboarding Guide

## Welcome to ORBIT-Pure Beta Testing! 🎵

**ORBIT-Pure** is a high-fidelity P2P music workstation designed for professional DJs, music librarians, and audio engineers. This guide will help you get started with the latest beta features and contribute effectively to the project's development.

---

## 🚀 Quick Start

### Installation
1. Download the latest release from the GitHub repository
2. Extract to your preferred location
3. Run `ORBIT.exe` (Windows) or `./ORBIT` (Linux/macOS)
4. Configure your Soulseek credentials in Settings

### First Run Checklist
- [ ] Set up your music library folders
- [ ] Configure Soulseek connection
- [ ] Run initial library scan
- [ ] Test playback with a few tracks
- [ ] Export a small playlist to verify CSV functionality

---

## 🛠️ New Beta Features Guide

### 1. Enhanced CSV Export with Forensic Data

**Purpose**: Professional-grade playlist export with audio integrity metrics for music library analysis.

#### How to Use:
1. Navigate to your playlist in the Library section
2. Right-click the playlist → "Export" → "Export to CSV with Forensics"
3. Choose your save location and filename
4. Open the CSV in Excel, Google Sheets, or any spreadsheet application

#### What You'll Get:
The CSV includes these forensic columns:
- **HighFreqEnergyDb**: High-frequency energy (16kHz+) in decibels
- **LowFreqEnergyDb**: Low-frequency energy (1kHz-15kHz) in decibels
- **EnergyRatio**: Ratio between high and low frequency energy
- **IsTranscoded**: Whether the file shows signs of transcoding
- **ForensicReason**: Detailed explanation of any integrity issues

#### Beta Testing Focus:
- Compare EnergyRatio values across different genres
- Report any "False Positive" transcoding flags on legitimate files
- Share anonymized CSV samples to help tune detection algorithms

---

### 2. Delta Scan Optimization

**Purpose**: Dramatically faster library synchronization for large collections.

#### How It Works:
- Only scans folders that have been modified since the last sync
- Compares file timestamps to determine what needs updating
- Maintains full database integrity while skipping unchanged content

#### How to Use:
1. Go to Library → Tools → "Sync Physical Library"
2. The system will automatically detect changes
3. Review the sync summary for added/removed/modified files

#### Performance Expectations:
- **Before**: Full scan of 10,000+ files = 5-15 minutes
- **After**: Incremental scan = 10-30 seconds
- **Best Case**: No changes = instant completion

#### Beta Testing Focus:
- Time sync operations before and after adding new music
- Report any files that aren't detected as changed when they should be
- Test with network drives or external storage

---

### 3. Global Exception Handling & Error Reporting

**Purpose**: User-friendly crash reporting for beta testing feedback.

#### What Happens During Errors:
1. Instead of crashing, you'll see the ORBIT-Pure Error Dialog
2. The dialog shows a user-friendly error summary
3. Technical details are available for reporting
4. You can copy the full report to clipboard

#### How to Report Issues:
1. When you see an error dialog, click "Copy to Clipboard"
2. Paste the report into a GitHub issue
3. Include steps to reproduce the problem
4. Mention your system specs (OS, RAM, etc.)

#### Error Report Contents:
- Timestamp and error summary
- Full stack trace for developers
- Log file location for additional context
- System information (OS, .NET version, architecture)

#### Beta Testing Focus:
- Intentionally trigger errors to test the reporting system
- Report any crashes that bypass the error dialog
- Verify that "Continue" actually allows you to keep working

---

## 🔍 Forensic Health Reports

### Creating Your Library Health Report

1. Export your entire library to CSV with forensics
2. Open in spreadsheet software
3. Filter by `IsTranscoded = TRUE` to see potential issues
4. Sort by `EnergyRatio` to identify unusual frequency distributions

### Sharing Reports (Anonymized)
- Remove personal file paths
- Replace artist/track names with generic labels if desired
- Focus on technical metrics and patterns
- Share via GitHub issues or discussions

### What to Look For:
- **High EnergyRatio**: May indicate compressed or processed audio
- **Low EnergyRatio**: Could suggest missing high frequencies
- **Transcoding Flags**: Review these carefully - they might be false positives

---

## 🐛 Issue Reporting Best Practices

### Before Reporting:
- [ ] Check if the issue has already been reported
- [ ] Try to reproduce the issue consistently
- [ ] Test with a minimal set of files/settings
- [ ] Note your system specifications

### When Reporting:
- [ ] Use descriptive titles: "CSV Export fails when playlist contains special characters"
- [ ] Include the full error report when available
- [ ] Mention which beta build you're using
- [ ] Describe expected vs. actual behavior
- [ ] Include steps to reproduce

### Priority Levels:
- **Critical**: App crashes, data loss, security issues
- **High**: Major functionality broken, performance issues
- **Medium**: UI glitches, minor bugs
- **Low**: Cosmetic issues, enhancement requests

---

## 📊 Performance Benchmarking

### Library Sync Performance
- Track time for initial sync vs. delta syncs
- Note the number of files and total size
- Report any slowdowns or hangs

### Export Performance
- Time CSV exports with and without forensics
- Compare file sizes and generation time
- Test with playlists of different sizes (10, 100, 1000 tracks)

### Memory Usage
- Monitor RAM usage during large operations
- Report any memory leaks or excessive consumption
- Note system specs when reporting performance issues

---

## 🔧 Troubleshooting Common Issues

### Build/Installation Issues
- Ensure you have .NET 9.0 Runtime installed
- Check that all dependencies are available
- Verify file permissions for log and database directories

### Library Sync Issues
- Check file permissions on music folders
- Ensure network drives are accessible during sync
- Verify that files aren't locked by other applications

### Playback Issues
- Test with different file formats (MP3, FLAC, WAV)
- Check audio device settings
- Verify file integrity with forensic tools

### Soulseek Connection Issues
- Confirm username/password are correct
- Check firewall/antivirus settings
- Try different network conditions

---

## 🎯 Beta Testing Goals

### Primary Objectives:
1. **Stability**: Identify and resolve crashes, hangs, and data corruption
2. **Performance**: Optimize sync times and memory usage for large libraries
3. **Accuracy**: Refine forensic detection algorithms
4. **Usability**: Polish the user interface and workflows

### Success Metrics:
- Zero crashes during normal usage
- Library sync completes within 30 seconds for incremental changes
- Forensic detection accuracy >95% (minimal false positives)
- All major features work reliably across different systems

---

## 📞 Getting Help

### Community Support:
- **GitHub Issues**: For bugs and feature requests
- **GitHub Discussions**: For general questions and feedback
- **Documentation**: Check the README.md and FEATURES.md files

### When to Contact:
- For urgent security issues: Create a private security advisory
- For complex bugs: Provide detailed reproduction steps
- For feature requests: Start a discussion thread first
- For general help: Check existing issues and documentation

---

## 🙏 Thank You!

Your participation in the ORBIT-Pure beta program is invaluable. By testing these features and providing detailed feedback, you're helping build the most reliable and powerful music workstation available.

**Remember**: The goal is to create a "Pure" experience - high-fidelity audio, rock-solid stability, and professional-grade tools for music professionals.

Happy testing! 🎵✨</content>
<parameter name="filePath">c:\Users\quint\OneDrive\Documenten\GitHub\ORBIT-Pure\BETA_TESTER_GUIDE.md