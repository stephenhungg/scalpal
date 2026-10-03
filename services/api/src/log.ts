// Minimal structured logger (JSON lines in production, readable in dev).

type Fields = Record<string, unknown>;

const pretty = process.env.NODE_ENV !== 'production';

function write(level: string, msg: string, fields?: Fields) {
  if (pretty) {
    const extra = fields && Object.keys(fields).length ? ' ' + JSON.stringify(fields) : '';
    const out = `${new Date().toISOString()} ${level.padEnd(5)} ${msg}${extra}`;
    (level === 'error' ? console.error : console.log)(out);
  } else {
    console.log(JSON.stringify({ t: new Date().toISOString(), level, msg, ...fields }));
  }
}

export const log = {
  info: (msg: string, fields?: Fields) => write('info', msg, fields),
  warn: (msg: string, fields?: Fields) => write('warn', msg, fields),
  error: (msg: string, fields?: Fields) => write('error', msg, fields),
};
