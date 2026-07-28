# Security Policy

## Supported Versions

| Version | Supported          |
| ------- | ------------------ |
| 1.0.x   | :white_check_mark: |
| < 1.0   | :x:                |

## Reporting a Vulnerability

We take the security of Kite Glance seriously. If you believe you've found a security vulnerability, please follow these steps:

### **DO NOT** file a public issue on GitHub

### How to Report

1. **Email**: Send your report to the maintainer at [contact email from README]
2. **Subject Line**: Use `[Security] Kite Glance - Brief Description`
3. **Include**:
   - Description of the vulnerability
   - Steps to reproduce (if applicable)
   - Potential impact
   - Suggested fix (if you have one)

### What to Expect

- **Acknowledgment**: Within 48 hours
- **Status Update**: Within 5 business days
- **Resolution Timeline**: Depends on severity
  - Critical: 24-72 hours
  - High: 1 week
  - Medium: 2 weeks
  - Low: Next release cycle

### Security Considerations in Kite Glance

The following areas are security-sensitive:

1. **Credential Storage**: Uses Windows DPAPI for encryption
2. **OAuth Flow**: Loopback server for token exchange (no external redirect)
3. **API Communication**: HTTPS-only with Kite Connect API
4. **Local Data**: No telemetry, no third-party servers

### Known Security Features

- Credentials encrypted at rest with per-user DPAPI
- Access tokens stored separately from API credentials
- No logging of sensitive data (tokens, credentials, holdings values)
- Single-instance mutex prevents duplicate processes
- Environment variables override stored credentials for development

### Responsible Disclosure

We appreciate responsible disclosure and will:
- Credit researchers who report valid vulnerabilities (unless they prefer anonymity)
- Keep you informed of our progress
- Coordinate public disclosure timing with you

Thank you for helping keep Kite Glance secure!
