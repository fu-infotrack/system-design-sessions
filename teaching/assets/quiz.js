/* Reusable quiz components.
   Quiz.sort({mount, prompt, buckets:[a,b], items:[{text, answer, why}]})

   Buckets are rendered as equal-width buttons in a fixed order, so button
   position and size give nothing away about the answer. */

const Quiz = (() => {
  const el = (t, cls, html) => {
    const n = document.createElement(t);
    if (cls) n.className = cls;
    if (html != null) n.innerHTML = html;
    return n;
  };

  function sort({ mount, prompt, buckets, items }) {
    const root = typeof mount === 'string' ? document.querySelector(mount) : mount;
    root.classList.add('quiz');
    let done = 0, right = 0;

    if (prompt) root.appendChild(el('p', 'quiz-prompt', prompt));
    const score = el('p', 'quiz-score', `0 / ${items.length}`);

    items.forEach((item) => {
      const card = el('div', 'quiz-item');
      card.appendChild(el('p', 'quiz-text', item.text));

      const row = el('div', 'quiz-buttons');
      const fb = el('div', 'quiz-feedback');

      buckets.forEach((b) => {
        const btn = el('button', 'quiz-btn', b);
        btn.type = 'button';
        btn.addEventListener('click', () => {
          if (card.dataset.answered) return;
          card.dataset.answered = '1';
          const ok = b === item.answer;
          if (ok) right++;
          done++;
          btn.classList.add(ok ? 'is-right' : 'is-wrong');
          row.querySelectorAll('.quiz-btn').forEach((x) => {
            x.disabled = true;
            if (x.textContent === item.answer) x.classList.add('is-answer');
          });
          fb.innerHTML = `<strong>${ok ? 'Yes' : 'Not quite'} — ${item.answer}.</strong> ${item.why}`;
          fb.classList.add('is-shown', ok ? 'was-right' : 'was-wrong');
          score.textContent = `${right} / ${items.length}`;
          if (done === items.length) {
            score.classList.add('is-complete');
            score.textContent += right === items.length
              ? '  — all correct.'
              : '  — revisit the ones you missed; the reasoning matters more than the score.';
          }
        });
        row.appendChild(btn);
      });

      card.appendChild(row);
      card.appendChild(fb);
      root.appendChild(card);
    });

    root.appendChild(score);
  }

  /* Quiz.choice({mount, prompt, items:[{text, options:[…], answer, why}]})
     Single-select from N options, stacked full-width so length gives nothing
     away. Use when two buckets cannot express the question. */
  function choice({ mount, prompt, items }) {
    const root = typeof mount === 'string' ? document.querySelector(mount) : mount;
    root.classList.add('quiz');
    let done = 0, right = 0;

    if (prompt) root.appendChild(el('p', 'quiz-prompt', prompt));
    const score = el('p', 'quiz-score', `0 / ${items.length}`);

    items.forEach((item) => {
      const card = el('div', 'quiz-item');
      card.appendChild(el('p', 'quiz-text', item.text));

      const list = el('div', 'quiz-options');
      const fb = el('div', 'quiz-feedback');

      item.options.forEach((opt) => {
        const btn = el('button', 'quiz-opt', opt);
        btn.type = 'button';
        btn.addEventListener('click', () => {
          if (card.dataset.answered) return;
          card.dataset.answered = '1';
          const ok = opt === item.answer;
          if (ok) right++;
          done++;
          btn.classList.add(ok ? 'is-right' : 'is-wrong');
          list.querySelectorAll('.quiz-opt').forEach((x) => {
            x.disabled = true;
            if (x.textContent === item.answer) x.classList.add('is-answer');
          });
          fb.innerHTML = `<strong>${ok ? 'Yes' : 'Not quite'} — ${item.answer}.</strong> ${item.why}`;
          fb.classList.add('is-shown', ok ? 'was-right' : 'was-wrong');
          score.textContent = `${right} / ${items.length}`;
          if (done === items.length) {
            score.classList.add('is-complete');
            score.textContent += right === items.length
              ? '  — all correct.'
              : '  — revisit the ones you missed; the reasoning matters more than the score.';
          }
        });
        list.appendChild(btn);
      });

      card.appendChild(list);
      card.appendChild(fb);
      root.appendChild(card);
    });

    root.appendChild(score);
  }

  return { sort, choice };
})();
