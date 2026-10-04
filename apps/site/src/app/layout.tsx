import type { Metadata } from "next";
import { Geist_Mono, Instrument_Serif } from "next/font/google";
import "./globals.css";

const serif = Instrument_Serif({
  variable: "--font-serif",
  subsets: ["latin"],
  weight: "400",
  style: ["normal", "italic"],
});

const mono = Geist_Mono({
  variable: "--font-mono",
  subsets: ["latin"],
  weight: ["400", "500", "600"],
});

export const metadata: Metadata = {
  title: "Scalpal",
  description:
    "A mixed reality operating room experience guided by Jarvis, your all-knowing assistant.",
};

export default function RootLayout({ children }: { children: React.ReactNode }) {
  return (
    <html lang="en" className={`${serif.variable} ${mono.variable} antialiased`}>
      <body>{children}</body>
    </html>
  );
}
