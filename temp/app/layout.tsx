import type { Metadata } from 'next';
import './globals.css';

export const metadata: Metadata = {
  title: 'X.OS — Ticket System',
  description: 'Unified ticketing: incidents and feedback in one queue.',
};

export default function RootLayout({ children }: { children: React.ReactNode }) {
  return (
    <html lang="en" data-theme="light">
      <body>{children}</body>
    </html>
  );
}
