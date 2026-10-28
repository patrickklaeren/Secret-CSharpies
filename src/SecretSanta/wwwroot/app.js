window.santa = {
    localDate(element, iso) {
        element.textContent = new Intl.DateTimeFormat(undefined, {day: 'numeric', month: 'short', year: 'numeric'}).format(new Date(iso));
        element.title = `${new Date(iso).toLocaleString()} (event boundary: midnight UTC)`;
    },
    copy(text) {
        return navigator.clipboard.writeText(text);
    },
    startWinter(element, files, participants) {
        this.stopWinter(element);
        if (!files.length) return;
        const reducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)');
        const rink = element.querySelector('.skaters');
        const state = {timer: null, reducedMotion};
        const schedule = () => {
            state.timer = setTimeout(skate, 1000 + Math.random() * 1000);
        };
        const skate = async () => {
            if (!element.isConnected) return this.stopWinter(element);
            if (reducedMotion.matches || document.hidden) {
                schedule();
                return;
            }
            const skater = document.createElement('div');
            const trick = document.createElement('div');
            const sprite = document.createElement('div');
            sprite.className = 'padoru-sprite';
            const img = document.createElement('img');
            img.alt = '';
            img.src = files[Math.floor(Math.random() * files.length)];
            if (Math.random() < 1 / 6) {
                try {
                    const avatarUrl = await participants.invokeMethodAsync('RandomParticipantAvatar');
                    if (avatarUrl) {
                        const avatar = document.createElement('img');
                        avatar.alt = '';
                        avatar.className = 'padoru-avatar';
                        avatar.src = avatarUrl;
                        await avatar.decode();
                        sprite.append(avatar);
                        img.src = '/assets/padoru/padoru-blank.webp';
                    }
                } catch {
                    // Keep the pre-built skater if the avatar or circuit is unavailable.
                }
            }
            if (element.winterState !== state || !element.isConnected) return;
            if (reducedMotion.matches || document.hidden) {
                schedule();
                return;
            }
            const spin = Math.random() < 0.09, jump = Math.random() < 0.12;
            trick.className = `padoru-trick${spin ? ' spin' : ''}${jump ? ' jump' : ''}`;
            skater.className = `skater skating${Math.random() < 0.5 ? ' reverse' : ''}`;
            const height = Math.random();
            const bottom = 2 + height * 20;
            skater.style.bottom = `${bottom}%`;
            trick.style.scale = `${1 - height * 0.3}`;
            if (Math.random() < 0.45) {
                const keyframes = [bottom, ...Array.from({length: 3}, () =>
                    Math.max(2, Math.min(22, bottom + (Math.random() * 2 - 1) * 0.8)))];
                const timing = {duration: 5000 + Math.random() * 3000, iterations: Infinity, direction: 'alternate'};
                skater.animate(keyframes.map(value => ({bottom: `${value}%`, easing: 'ease-in-out'})), timing);
                trick.animate(keyframes.map(value => ({scale: `${1 - (value - 2) / 20 * 0.3}`, easing: 'ease-in-out'})), timing);
            }
            sprite.append(img);
            trick.append(sprite);
            skater.append(trick);
            skater.addEventListener('animationend', event => {
                if (event.target === skater) skater.remove();
            });
            rink.append(skater);
            schedule();
        };
        state.motionChanged = () => {
            if (reducedMotion.matches) rink.replaceChildren();
        };
        reducedMotion.addEventListener('change', state.motionChanged);
        element.winterState = state;
        state.timer = setTimeout(skate, 3500 + Math.random() * 4000);
    },
    stopWinter(element) {
        const state = element.winterState;
        if (!state) return;
        clearTimeout(state.timer);
        state.reducedMotion.removeEventListener('change', state.motionChanged);
        element.querySelector('.skaters').replaceChildren();
        delete element.winterState;
    }
};
