const { readFileSync } = require('node:fs');
const { join } = require('node:path');
const vm = require('node:vm');
const assert = require('node:assert/strict');

// A small DOM/timer harness verifies overlapping passes and their cleanup without waiting in real time.
function node() {
    return { children: [], style: {}, listeners: {},
        append(child) { this.children.push(child); child.parent = this; },
        replaceChildren() { this.children = []; },
        addEventListener(name, callback) { this.listeners[name] = callback; },
        remove() { this.parent.children = this.parent.children.filter(child => child !== this); }
    };
}
const rink = node();
const element = { isConnected: true, querySelector: () => rink };
const motion = { matches: false, addEventListener() {}, removeEventListener() {} };
const timers = new Map();
let timerId = 0;
const context = {
    window: { matchMedia: () => motion },
    document: { hidden: false, createElement: node },
    setTimeout(fn, delay) { const id = ++timerId; timers.set(id, { fn, delay }); return id; },
    clearTimeout(id) { timers.delete(id); }
};
vm.runInNewContext(readFileSync(join(__dirname, '../src/SecretSanta/wwwroot/app.js'), 'utf8'), context);
const santa = context.window.santa;
function tick() {
    const id = element.winterState.timer;
    const { fn } = timers.get(id);
    timers.delete(id);
    fn();
}
santa.startWinter(element, ['/test-padoru.webp']);
tick();
assert.equal(rink.children.length, 1);
assert.ok(timers.get(element.winterState.timer).delay >= 6000);
assert.ok(timers.get(element.winterState.timer).delay < 14000);
tick();
assert.equal(rink.children.length, 2, 'A new pass does not replace an existing skater');
const first = rink.children[0];
first.listeners.animationend({ target: first.children[0] });
assert.equal(rink.children.length, 2, 'Trick animations must not remove the skater');
first.listeners.animationend({ target: first });
assert.equal(rink.children.length, 1);
motion.matches = true;
element.winterState.motionChanged();
assert.equal(rink.children.length, 0);
tick();
assert.equal(rink.children.length, 0, 'Reduced motion prevents new passes');
santa.stopWinter(element);
assert.equal(timers.size, 0);
assert.equal(element.winterState, undefined);
console.log('Winter animation checks passed.');
